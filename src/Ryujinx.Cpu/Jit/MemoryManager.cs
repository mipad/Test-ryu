using ARMeilleure.Memory;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.Jit
{
    /// <summary>
    /// Represents a CPU memory manager.
    /// </summary>
    public sealed class MemoryManager : VirtualMemoryManagerRefCountedBase, ICpuMemoryManager, IVirtualMemoryManagerTracked
    {
        private const int PteSize = 8;
        private const int PointerTagBit = 62;

        private readonly MemoryBlock _backingMemory;
        private readonly InvalidAccessHandler _invalidAccessHandler;

        /// <inheritdoc/>
        public bool UsesPrivateAllocations => false;

        /// <summary>
        /// Address space width in bits.
        /// </summary>
        public int AddressSpaceBits { get; }

        private readonly MemoryBlock _pageTable;
        private readonly ManagedPageFlags _pages;

        /// <summary>
        /// Page table base pointer.
        /// </summary>
        public IntPtr PageTablePointer => _pageTable.Pointer;

        public MemoryManagerType Type => MemoryManagerType.SoftwarePageTable;
        public MemoryTracking Tracking { get; }
        public event Action<ulong, ulong> UnmapEvent;
        protected override ulong AddressSpaceSize { get; }

        /// <summary>
        /// Creates a new instance of the memory manager.
        /// </summary>
        public MemoryManager(MemoryBlock backingMemory, ulong addressSpaceSize, InvalidAccessHandler invalidAccessHandler = null)
        {
            _backingMemory = backingMemory;
            _invalidAccessHandler = invalidAccessHandler;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;
            AddressSpaceSize = asSize;
            _pageTable = new MemoryBlock((asSize / PageSize) * PteSize);
            _pages = new ManagedPageFlags(AddressSpaceBits);
            Tracking = new MemoryTracking(this, PageSize);
        }

        /// <summary>
        /// Enhanced address validation with detailed logging.
        /// </summary>
        private bool ValidateAddressInternal(ulong va)
        {
            bool isValid = va < AddressSpaceSize;
            
            if (!isValid)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"Invalid VA: 0x{va:X16} (AddressSpaceSize: 0x{AddressSpaceSize:X16})");
            }
            
            return isValid;
        }

        /// <inheritdoc/>
        public bool ValidateAddressAndSize(ulong address, ulong size)
        {
            if (size == 0)
            {
                return true;
            }

            if (address + size < address)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"Address overflow: 0x{address:X16} + 0x{size:X16}");
                return false;
            }

            bool isValid = address + size <= AddressSpaceSize;
            
            if (!isValid)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Address range invalid: 0x{address:X16}-0x{address + size:X16} " +
                    $"(AddressSpaceSize: 0x{AddressSpaceSize:X16})");
            }
            
            return isValid;
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            AssertValidAddressAndSize(va, size);

            if (!_backingMemory.IsValid(pa, size))
            {
                throw new ArgumentOutOfRangeException(nameof(pa), 
                    $"Physical address 0x{pa:X16} with size 0x{size:X16} is invalid");
            }

            ulong remainingSize = size;
            ulong oVa = va;
            while (remainingSize != 0)
            {
                _pageTable.Write((va / PageSize) * PteSize, PaToPte(pa));
                va += PageSize;
                pa += PageSize;
                remainingSize -= PageSize;
            }

            _pages.AddMapping(oVa, size);
            Tracking.Map(oVa, size);
        }

        /// <inheritdoc/>
        public void Unmap(ulong va, ulong size)
        {
            if (size == 0)
            {
                return;
            }

            AssertValidAddressAndSize(va, size);

            UnmapEvent?.Invoke(va, size);
            Tracking.Unmap(va, size);
            _pages.RemoveMapping(va, size);

            ulong remainingSize = size;
            while (remainingSize != 0)
            {
                _pageTable.Write((va / PageSize) * PteSize, 0UL);
                va += PageSize;
                remainingSize -= PageSize;
            }
        }

        /// <summary>
        /// Enhanced invalid access handler with recovery mechanism.
        /// </summary>
        private bool HandleInvalidAccess(ulong va)
        {
            if (_invalidAccessHandler == null)
            {
                return false;
            }

            try
            {
                // Try to let the handler recover
                bool recovered = _invalidAccessHandler(va);
                
                if (recovered)
                {
                    Logger.Warning?.Print(LogClass.Cpu, 
                        $"Recovered from invalid access at VA: 0x{va:X16}");
                }
                
                return recovered;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Cpu, 
                    $"Error in invalid access handler for VA 0x{va:X16}: {ex}");
                return false;
            }
        }

        public override T ReadTracked<T>(ulong va)
        {
            try
            {
                return base.ReadTracked<T>(va);
            }
            catch (InvalidMemoryRegionException)
            {
                if (!HandleInvalidAccess(va))
                {
                    throw;
                }
                return default;
            }
        }

        /// <inheritdoc/>
        public T ReadGuest<T>(ulong va) where T : unmanaged
        {
            try
            {
                SignalMemoryTrackingImpl(va, (ulong)Unsafe.SizeOf<T>(), false, true);
                return Read<T>(va);
            }
            catch (InvalidMemoryRegionException)
            {
                if (!HandleInvalidAccess(va))
                {
                    throw;
                }
                return default;
            }
        }

        /// <inheritdoc/>
        public override void Read(ulong va, Span<byte> data)
        {
            try
            {
                base.Read(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (!HandleInvalidAccess(va))
                {
                    throw;
                }
            }
        }

        public override void Write(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                base.Write(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (!HandleInvalidAccess(va))
                {
                    throw;
                }
            }
        }

        /// <inheritdoc/>
        public void WriteGuest<T>(ulong va, T value) where T : unmanaged
        {
            Span<byte> data = MemoryMarshal.Cast<T, byte>(MemoryMarshal.CreateSpan(ref value, 1));
            SignalMemoryTrackingImpl(va, (ulong)data.Length, true, true);
            Write(va, data);
        }

        public override void WriteUntracked(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                base.WriteUntracked(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (!HandleInvalidAccess(va))
                {
                    throw;
                }
            }
        }

        public override ReadOnlySequence<byte> GetReadOnlySequence(ulong va, int size, bool tracked = false)
        {
            try
            {
                return base.GetReadOnlySequence(va, size, tracked);
            }
            catch (InvalidMemoryRegionException)
            {
                if (!HandleInvalidAccess(va))
                {
                    throw;
                }
                return ReadOnlySequence<byte>.Empty;
            }
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);
            return ref _backingMemory.GetRef<T>(GetPhysicalAddressInternal(va));
        }

        /// <inheritdoc/>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<HostMemoryRange>();
            }

            var guestRegions = GetPhysicalRegionsImpl(va, size);
            if (guestRegions == null)
            {
                return null;
            }

            var regions = new HostMemoryRange[guestRegions.Count];
            for (int i = 0; i < regions.Length; i++)
            {
                var guestRegion = guestRegions[i];
                IntPtr pointer = _backingMemory.GetPointer(guestRegion.Address, guestRegion.Size);
                regions[i] = new HostMemoryRange((nuint)(ulong)pointer, guestRegion.Size);
            }

            return regions;
        }

        /// <inheritdoc/>
        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<MemoryRange>();
            }

            return GetPhysicalRegionsImpl(va, size);
        }

        private List<MemoryRange> GetPhysicalRegionsImpl(ulong va, ulong size)
        {
            if (!ValidateAddress(va) || !ValidateAddressAndSize(va, size))
            {
                return null;
            }

            int pages = GetPagesCount(va, (uint)size, out va);
            var regions = new List<MemoryRange>();

            ulong regionStart = GetPhysicalAddressInternal(va);
            ulong regionSize = PageSize;

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return null;
                }

                ulong newPa = GetPhysicalAddressInternal(va + PageSize);

                if (GetPhysicalAddressInternal(va) + PageSize != newPa)
                {
                    regions.Add(new MemoryRange(regionStart, regionSize));
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += PageSize;
            }

            regions.Add(new MemoryRange(regionStart, regionSize));
            return regions;
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            if (size == 0UL)
            {
                return true;
            }

            if (!ValidateAddressAndSize(va, size))
            {
                return false;
            }

            int pages = GetPagesCount(va, (uint)size, out va);
            for (int page = 0; page < pages; page++)
            {
                if (!IsMapped(va))
                {
                    return false;
                }
                va += PageSize;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            if (!ValidateAddressInternal(va))
            {
                return false;
            }

            return _pageTable.Read<ulong>((va / PageSize) * PteSize) != 0;
        }

        private nuint GetPhysicalAddressInternal(ulong va)
        {
            ulong pte = _pageTable.Read<ulong>((va / PageSize) * PteSize) & ~(0xffffUL << 48);
            ulong pa = PteToPa(pte) + (va & PageMask);
            
            if (!_backingMemory.IsValid((nuint)pa, 1))
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Invalid PA translation: VA=0x{va:X16} -> PA=0x{pa:X16}");
                throw new InvalidMemoryRegionException();
            }
            
            return (nuint)pa;
        }

        /// <inheritdoc/>
        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            // TODO: Implement proper memory protection
        }

        /// <inheritdoc/>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            AssertValidAddressAndSize(va, size);

            if (guest)
            {
                protection = (~protection) & MemoryPermission.ReadAndWrite;
                long tag = protection switch
                {
                    MemoryPermission.None => 0L,
                    MemoryPermission.Write => 2L << PointerTagBit,
                    _ => 3L << PointerTagBit,
                };

                int pages = GetPagesCount(va, (uint)size, out va);
                ulong pageStart = va >> PageBits;
                long invTagMask = ~(0xffffL << 48);

                for (int page = 0; page < pages; page++)
                {
                    ref long pageRef = ref _pageTable.GetRef<long>(pageStart * PteSize);
                    long pte;

                    do
                    {
                        pte = Volatile.Read(ref pageRef);
                    }
                    while (pte != 0 && Interlocked.CompareExchange(ref pageRef, (pte & invTagMask) | tag, pte) != pte);

                    pageStart++;
                }
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
        }

        /// <inheritdoc/>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags = RegionFlags.None)
        {
            return Tracking.BeginTracking(address, size, id, flags);
        }

        /// <inheritdoc/>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags = RegionFlags.None)
        {
            return Tracking.BeginGranularTracking(address, size, handles, granularity, id, flags);
        }

        /// <inheritdoc/>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            return Tracking.BeginSmartGranularTracking(address, size, granularity, id);
        }

        private void SignalMemoryTrackingImpl(ulong va, ulong size, bool write, bool guest, bool precise = false, int? exemptId = null)
        {
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            if (guest)
            {
                long tag = (write ? 3L : 1L) << PointerTagBit;
                int pages = GetPagesCount(va, (uint)size, out _);
                ulong pageStart = va >> PageBits;

                for (int page = 0; page < pages; page++)
                {
                    ref long pageRef = ref _pageTable.GetRef<long>(pageStart * PteSize);
                    long pte = Volatile.Read(ref pageRef);

                    if ((pte & tag) != 0)
                    {
                        Tracking.VirtualMemoryEvent(va, size, write, precise: false, exemptId, true);
                        break;
                    }

                    pageStart++;
                }
            }
            else
            {
                _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
            }
        }

        /// <inheritdoc/>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            SignalMemoryTrackingImpl(va, size, write, false, precise, exemptId);
        }

        private ulong PaToPte(ulong pa)
        {
            if (!_backingMemory.IsValid((nuint)pa, 1))
            {
                throw new ArgumentOutOfRangeException(nameof(pa), $"Invalid physical address: 0x{pa:X16}");
            }
            return (ulong)_backingMemory.GetPointer(pa, PageSize);
        }

        private ulong PteToPa(ulong pte)
        {
            ulong pa = (ulong)((long)pte - _backingMemory.Pointer.ToInt64());
            
            if (!_backingMemory.IsValid((nuint)pa, 1))
            {
                throw new InvalidOperationException($"Invalid PA from PTE: 0x{pa:X16}");
            }
            
            return pa;
        }

        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy()
        {
            _pageTable.Dispose();
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
        {
            if (!_backingMemory.IsValid(pa, size))
            {
                throw new ArgumentOutOfRangeException(nameof(pa), $"Invalid physical address range: 0x{pa:X16}-0x{pa + (ulong)size:X16}");
            }
            return _backingMemory.GetMemory(pa, size);
        }

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
        {
            if (!_backingMemory.IsValid(pa, size))
            {
                throw new ArgumentOutOfRangeException(nameof(pa), $"Invalid physical address range: 0x{pa:X16}-0x{pa + (ulong)size:X16}");
            }
            return _backingMemory.GetSpan(pa, size);
        }

        protected override nuint TranslateVirtualAddressChecked(ulong va)
        {
            return GetPhysicalAddressInternal(va);
        }

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            return GetPhysicalAddressInternal(va);
        }
    }
}
