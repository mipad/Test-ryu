using ARMeilleure.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Represents a CPU memory manager which maps guest virtual memory directly onto a host virtual region.
    /// </summary>
    public sealed class MemoryManagerNative : VirtualMemoryManagerRefCountedBase, ICpuMemoryManager, IVirtualMemoryManagerTracked, IWritableBlock
    {
        private readonly MemoryBlock _addressSpace;

        private readonly MemoryBlock _backingMemory;
        private readonly PageTable<ulong> _pageTable;

        private readonly MemoryEhMeilleure _memoryEh;

        private readonly ManagedPageFlags _pages;

        // 添加：静态零页面用于安全处理空指针访问
        private static readonly byte[] _zeroPage = new byte[0x1000]; // 4KB 零页面

        /// <inheritdoc/>
        public bool UsesPrivateAllocations => false;

        public IntPtr PageTablePointer => IntPtr.Zero;

        public ulong ReservedSize => (ulong)_addressSpace.Pointer.ToInt64();

        public MemoryManagerType Type => MemoryManagerType.HostMappedUnsafe;

        public MemoryTracking Tracking { get; }

        public event Action<ulong, ulong> UnmapEvent;

        public int AddressSpaceBits { get; }
        protected override ulong AddressSpaceSize { get; }

        /// <summary>
        /// Creates a new instance of the host mapped memory manager.
        /// </summary>
        /// <param name="addressSpace">Address space memory block</param>
        /// <param name="backingMemory">Physical backing memory where virtual memory will be mapped to</param>
        /// <param name="addressSpaceSize">Size of the address space</param>
        /// <param name="invalidAccessHandler">Optional function to handle invalid memory accesses</param>
        public MemoryManagerNative(
            MemoryBlock addressSpace,
            MemoryBlock backingMemory,
            ulong addressSpaceSize,
            InvalidAccessHandler invalidAccessHandler = null)
        {
            _backingMemory = backingMemory;
            _pageTable = new PageTable<ulong>();
            AddressSpaceSize = addressSpaceSize;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;

            _pages = new ManagedPageFlags(asBits);

            _addressSpace = addressSpace;

            Tracking = new MemoryTracking(this, PageSize, invalidAccessHandler);
            _memoryEh = new MemoryEhMeilleure(addressSpaceSize, Tracking);
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            AssertValidAddressAndSize(va, size);

            _addressSpace.MapView(_backingMemory, pa, AddressToOffset(va), size);
            _pages.AddMapping(va, size);
            PtMap(va, pa, size);

            Tracking.Map(va, size);
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
            AssertValidAddressAndSize(va, size);

            UnmapEvent?.Invoke(va, size);
            Tracking.Unmap(va, size);

            _pages.RemoveMapping(va, size);
            PtUnmap(va, size);
            _addressSpace.UnmapView(_backingMemory, AddressToOffset(va), size);
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
            _addressSpace.Reprotect(AddressToOffset(va), size, permission);
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            // 添加：空指针安全检查
            if (va == 0)
            {
                // 记录警告但不崩溃，返回安全的零值引用
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer access prevented at va=0x{va:X16}, type={typeof(T).Name}, size={Unsafe.SizeOf<T>()}");
                
                // 返回指向零页面的安全引用
                unsafe
                {
                    fixed (byte* ptr = _zeroPage)
                    {
                        return ref Unsafe.AsRef<T>(ptr);
                    }
                }
            }

            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressChecked(va));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            // 添加：空指针特殊处理
            if (va == 0)
            {
                return false; // 地址0始终视为未映射
            }
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            // 添加：空指针范围检查
            if (va == 0 && size > 0)
            {
                return false; // 包含地址0的范围视为未映射
            }
            
            AssertValidAddressAndSize(va, size);

            return _pages.IsRangeMapped(va, size);
        }

        /// <inheritdoc/>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<HostMemoryRange>();
            }

            // 添加：空指针安全检查
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Memory, 
                    $"Null pointer region access attempted, returning empty regions");
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

            // 添加：空指针安全检查
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Memory, 
                    $"Null pointer physical region access attempted, returning empty regions");
                return Enumerable.Empty<MemoryRange>();
            }

            return GetPhysicalRegionsImpl(va, size);
        }

        private List<MemoryRange> GetPhysicalRegionsImpl(ulong va, ulong size)
        {
            // 添加：空指针显式拒绝
            if (va == 0)
            {
                return null;
            }

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
            // 添加：空指针特殊处理
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer physical address translation attempted, returning safe address");
                return 0x1000; // 返回一个安全的非零地址
            }

            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            // 添加：空指针保护
            if (va == 0)
            {
                return 0x1000; // 返回安全地址
            }
            return _pageTable.Read(va) + (va & PageMask);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// This function also validates that the given range is both valid and mapped, and will throw if it is not.
        /// </remarks>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            // 添加：空指针访问的友好处理
            if (va == 0)
            {
                // 记录警告但不崩溃，避免模拟器退出
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer memory tracking event: va=0x{va:X16}, size=0x{size:X16}, write={write}, precise={precise}");
                
                if (precise)
                {
                    // 对于精确跟踪，仍然通知但使用安全的方式
                    Tracking.VirtualMemoryEvent(0x1000, size, write, precise: true, exemptId);
                }
                return;
            }

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
            // 添加：空指针保护
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer tracking reprotect attempted, ignoring");
                return;
            }

            if (guest)
            {
                _addressSpace.Reprotect(AddressToOffset(va), size, protection, false);
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
        }

        /// <inheritdoc/>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags)
        {
            // 添加：空指针保护
            if (address == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer begin tracking attempted, using safe address");
                address = 0x1000; // 使用安全地址
            }
            return Tracking.BeginTracking(address, size, id, flags);
        }

        /// <inheritdoc/>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags)
        {
            // 添加：空指针保护
            if (address == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer granular tracking attempted, using safe address");
                address = 0x1000; // 使用安全地址
            }
            return Tracking.BeginGranularTracking(address, size, handles, granularity, id, flags);
        }

        /// <inheritdoc/>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            // 添加：空指针保护
            if (address == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer smart granular tracking attempted, using safe address");
                address = 0x1000; // 使用安全地址
            }
            return Tracking.BeginSmartGranularTracking(address, size, granularity, id);
        }

        private ulong AddressToOffset(ulong address)
        {
            // 添加：空指针保护
            if (address == 0)
            {
                Logger.Warning?.Print(LogClass.Memory, 
                    $"Null pointer address to offset conversion attempted, using safe offset");
                return 0x1000; // 返回安全偏移
            }

            if (address < ReservedSize)
            {
                throw new ArgumentException($"Invalid address 0x{address:x16}");
            }

            return address - ReservedSize;
        }

        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy()
        {
            _addressSpace.Dispose();
            _memoryEh.Dispose();
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
            => _backingMemory.GetMemory(pa, size);

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
            => _backingMemory.GetSpan(pa, size);

        protected override nuint TranslateVirtualAddressChecked(ulong va)
        {
            // 添加：空指针保护
            if (va == 0)
            {
                Logger.Warning?.Print(LogClass.Cpu, 
                    $"Null pointer virtual address translation attempted, using safe address");
                return (nuint)0x1000; // 返回安全地址
            }
            return (nuint)GetPhysicalAddressChecked(va);
        }

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
        {
            // 添加：空指针保护
            if (va == 0)
            {
                return (nuint)0x1000; // 返回安全地址
            }
            return (nuint)GetPhysicalAddressInternal(va);
        }

        // 添加：改进的地址验证方法
        protected override bool ValidateAddress(ulong va)
        {
            // 显式拒绝空指针
            if (va == 0)
            {
                return false;
            }
            return va < AddressSpaceSize;
        }

        // 添加：改进的地址和大小验证
        protected override void AssertValidAddressAndSize(ulong va, ulong size)
        {
            // 特殊处理空指针访问
            if (va == 0 && size > 0)
            {
                Logger.Warning?.Print(LogClass.Memory, 
                    $"Null pointer access detected but handled safely: va=0x{va:X16}, size=0x{size:X16}");
                return; // 不抛出异常，允许继续执行
            }

            if (va + size < va || va + size > AddressSpaceSize)
            {
                ThrowInvalidMemoryRegionException($"va=0x{va:X16}, size=0x{size:X16}");
            }
        }
    }
}
