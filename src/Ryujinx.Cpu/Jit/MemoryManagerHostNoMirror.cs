using ARMeilleure.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.Jit
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("android")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public sealed class MemoryManagerHostNoMirror : VirtualMemoryManagerRefCountedBase, ICpuMemoryManager, IVirtualMemoryManagerTracked, IWritableBlock
    {
#if LINUX || ANDROID || MACOS || WINDOWS
    // 目标平台字段
    private readonly MemoryBlock _addressSpace;
    private readonly MemoryBlock _backingMemory;
    private readonly PageTable<ulong> _pageTable;
    private readonly bool _unsafeMode;
    private readonly ManagedPageFlags _pages;
    // TODO: 后续实现内存异常处理时使用
    private readonly MemoryEhMeilleure _memoryEh;
    // TODO: 后续实现无效访问处理逻辑
    private readonly InvalidAccessHandler _invalidAccessHandler;
#else
    // 非目标平台默认值（仅用于消除警告）
    private readonly MemoryBlock _addressSpace = null;
    private readonly MemoryBlock _backingMemory = null;
    private readonly PageTable<ulong> _pageTable = null;
    private readonly bool _unsafeMode = false;
    private readonly ManagedPageFlags _pages = null;
    private readonly MemoryEhMeilleure _memoryEh = null;
    private readonly InvalidAccessHandler _invalidAccessHandler = null;
#endif

        public int AddressSpaceBits { get; }
        protected override ulong AddressSpaceSize { get; }

        public bool UsesPrivateAllocations => false;

        public IntPtr PageTablePointer
        {
            get
            {
#if LINUX || ANDROID || MACOS || WINDOWS
                return _addressSpace.Pointer;
#else
                throw new PlatformNotSupportedException();
#endif
            }
        }
        
        public MemoryManagerType Type => _unsafeMode ? MemoryManagerType.HostMappedUnsafe : MemoryManagerType.HostMapped;

        public MemoryTracking Tracking { get; }

#pragma warning disable CS0067
        public event Action<ulong, ulong> UnmapEvent; // TODO: 
#pragma warning restore CS0067

        /// <summary>
        /// Creates a new instance of the host mapped memory manager.
        /// </summary>
        /// <param name="addressSpace">Address space instance to use</param>
        /// <param name="unsafeMode">True if unmanaged access should not be masked (unsafe), false otherwise.</param>
        /// <param name="invalidAccessHandler">Optional function to handle invalid memory accesses</param>
        public MemoryManagerHostNoMirror(
            MemoryBlock addressSpace,
            MemoryBlock backingMemory,
            bool unsafeMode,
            InvalidAccessHandler invalidAccessHandler)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            _addressSpace = addressSpace;
            _backingMemory = backingMemory;
            _pageTable = new PageTable<ulong>();
            _invalidAccessHandler = invalidAccessHandler;
            _unsafeMode = unsafeMode;
            AddressSpaceSize = addressSpace.Size;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpace.Size)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;
            _pages = new ManagedPageFlags(asBits);
            Tracking = new MemoryTracking(this, (int)MemoryBlock.GetPageSize(), invalidAccessHandler);
            _memoryEh = new MemoryEhMeilleure(addressSpace, null, Tracking);
#else
            throw new PlatformNotSupportedException();
#endif
        }

        /// <summary>
        /// Ensures the combination of virtual address and size is part of the addressable space and fully mapped.
        /// </summary>
        /// <param name="va">Virtual address of the range</param>
        /// <param name="size">Size of the range in bytes</param>
        private void AssertMapped(ulong va, ulong size)
        {
            if (!ValidateAddressAndSize(va, size) || !_pages.IsRangeMapped(va, size))
            {
                throw new InvalidMemoryRegionException($"Not mapped: va=0x{va:X16}, size=0x{size:X16}");
            }
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
        #if LINUX || ANDROID || MACOS || WINDOWS
            AssertValidAddressAndSize(va, size);

            _addressSpace.MapView(_backingMemory, pa, va, size);
            _pages.AddMapping(va, size);
            PtMap(va, pa, size);

            Tracking.Map(va, size);
            #else
            throw new PlatformNotSupportedException("Map is not supported on this platform.");
#endif
        }

        private void PtMap(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Map(va, pa);

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        /// <inheritdoc/>
        public void Unmap(ulong va, ulong size)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            AssertValidAddressAndSize(va, size);

            UnmapEvent?.Invoke(va, size);
            Tracking.Unmap(va, size);

            _pages.RemoveMapping(va, size);
            PtUnmap(va, size);
            _addressSpace.UnmapView(_backingMemory, va, size);
#else
            throw new PlatformNotSupportedException("Unmap is not supported on this platform.");
#endif
        }

        private void PtUnmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Unmap(va);

                va += PageSize;
                size -= PageSize;
            }
        }

        /// <inheritdoc/>
        public void Reprotect(ulong va, ulong size, MemoryPermission permission)
{
#if LINUX || ANDROID || MACOS || WINDOWS
    // 
    throw new NotImplementedException("Reprotect is not implemented.");
#else
    throw new PlatformNotSupportedException("Reprotect is not supported on this platform.");
#endif
}

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressChecked(va));
#else
            throw new PlatformNotSupportedException("GetRef<T> is not supported on this platform.");
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            return _pages.IsRangeMapped(va, size);
        }

        /// <inheritdoc/>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            if (size == 0)
            {
                return Enumerable.Empty<HostMemoryRange>();
            }

            var guestRegions = GetPhysicalRegionsImpl(va, size);
            if (guestRegions == null)
            {
                return Enumerable.Empty<HostMemoryRange>();
            }

            var regions = new HostMemoryRange[guestRegions.Count];

            for (int i = 0; i < regions.Length; i++)
            {
                var guestRegion = guestRegions[i];
                IntPtr pointer = _backingMemory.GetPointer(guestRegion.Address, guestRegion.Size);
                regions[i] = new HostMemoryRange((nuint)(ulong)pointer, guestRegion.Size);
            }

            return regions;
#else
            throw new PlatformNotSupportedException("GetHostRegions is not supported on this platform.");
#endif
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

        private ulong GetPhysicalAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            return _pageTable.Read(va) + (va & PageMask);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// This function also validates that the given range is both valid and mapped, 和 will throw if it is not.
        /// </remarks>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
        }

        /// <inheritdoc/>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            if (guest)
            {
                _addressSpace.Reprotect(va, size, protection, false);
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
#else
            throw new PlatformNotSupportedException("TrackingReprotect is not supported on this platform.");
#endif
        }

        /// <inheritdoc/>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags)
        {
            return Tracking.BeginTracking(address, size, id, flags);
        }

        /// <inheritdoc/>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags)
        {
            return Tracking.BeginGranularTracking(address, size, handles, granularity, id, flags);
        }

        /// <inheritdoc/>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            return Tracking.BeginSmartGranularTracking(address, size, granularity, id);
        }

        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy()
        {
        #if LINUX || ANDROID || MACOS || WINDOWS
            _addressSpace.Dispose();
            _memoryEh.Dispose();
            #else
            throw new PlatformNotSupportedException("Destroy is not supported on this platform.");
#endif
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            return _backingMemory.GetMemory(pa, size);
#else
            throw new PlatformNotSupportedException("GetPhysicalAddressMemory is not supported on this platform.");
#endif
        }

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
        {
#if LINUX || ANDROID || MACOS || WINDOWS
            return _backingMemory.GetSpan(pa, size);
#else
            throw new PlatformNotSupportedException("GetPhysicalAddressSpan is not supported on this platform.");
#endif
        }

        protected override nuint TranslateVirtualAddressChecked(ulong va)
            => (nuint)GetPhysicalAddressChecked(va);

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
            => (nuint)GetPhysicalAddressInternal(va);
    }
}
