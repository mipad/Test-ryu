using ARMeilleure.Memory;
using Ryujinx.Common.System;  // 新增引用
using Ryujinx.Common.Utilities;  // 新增引用
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

// 新增命名空间用于 Android 优先级调整
#if ANDROID
using Android.OS;
#endif

namespace Ryujinx.Cpu.Jit
{
    /// <summary>
    /// Represents a CPU memory manager.
    /// </summary>
    public sealed class MemoryManager : VirtualMemoryManagerRefCountedBase, ICpuMemoryManager, IVirtualMemoryManagerTracked
    {
        private const int PteSize = 8;
        private const int PointerTagBit = 62;
        private const ulong LargePageSize = 2 * 1024 * 1024; // 2MB
        private const ulong LargePageFlag = 1UL << 52; // 自定义大页标志位

        // 音频缓冲区配置（需要根据游戏调整）
        private const ulong AudioBufferBase = 0x20000000; // 典型音频缓冲区起始地址
        private const ulong AudioBufferSize = 0x100000;   // 1MB 典型大小
        private const int AudioThreadId = 18; // 根据日志中的 "Thread: CRI Server Manager"

        private readonly MemoryBlock _backingMemory;
        private readonly InvalidAccessHandler _invalidAccessHandler;
        private readonly RateLimiter _audioRateLimiter; // 音频带宽限制器

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
        /// <param name="backingMemory">Physical backing memory where virtual memory will be mapped to</param>
        /// <param name="addressSpaceSize">Size of the address space</param>
        /// <param name="invalidAccessHandler">Optional function to handle invalid memory accesses</param>
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
            
            // 将音频检测函数传递给MemoryTracking构造函数
            Tracking = new MemoryTracking(this, PageSize, null, false, IsAudioBuffer);

            // 初始化带宽限制器（300MB/s）
            _audioRateLimiter = new RateLimiter(300 * 1024 * 1024);
        }

        // 检测是否为音频缓冲区
        private bool IsAudioBuffer(ulong va, ulong size)
        {
            return va >= AudioBufferBase && 
                   (va + size) <= (AudioBufferBase + AudioBufferSize);
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            AssertValidAddressAndSize(va, size);

            ulong oVa = va;

            // 为音频缓冲区启用大页映射
            if (IsAudioBuffer(va, size) && size >= LargePageSize)
            {
                // 使用2MB大页映射
                ulong largePageCount = size / LargePageSize;
                for (ulong i = 0; i < largePageCount; i++)
                {
                    _pageTable.Write(((va + i * LargePageSize) / PageSize) * PteSize, 
                                    PaToPte(pa + i * LargePageSize) | LargePageFlag);
                }
                
                // 处理剩余部分
                ulong remaining = size % LargePageSize;
                if (remaining > 0)
                {
                    va += largePageCount * LargePageSize;
                    pa += largePageCount * LargePageSize;
                    ulong remainingSize = remaining;
                    while (remainingSize != 0)
                    {
                        _pageTable.Write((va / PageSize) * PteSize, PaToPte(pa));
                        va += PageSize;
                        pa += PageSize;
                        remainingSize -= PageSize;
                    }
                }
            }
            else
            {
                // 标准小页映射
                ulong remainingSize = size;
                while (remainingSize != 0)
                {
                    _pageTable.Write((va / PageSize) * PteSize, PaToPte(pa));
                    va += PageSize;
                    pa += PageSize;
                    remainingSize -= PageSize;
                }
            }

            _pages.AddMapping(oVa, size);
            Tracking.Map(oVa, size);
        }

        /// <inheritdoc/>
        public void Unmap(ulong va, ulong size)
        {
            // If size is 0, there's nothing to unmap, just exit early.
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

        public override T ReadTracked<T>(ulong va)
        {
            try
            {
                return base.ReadTracked<T>(va);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
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
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }

                return default;
            }
        }

        /// <inheritdoc/>
        public override void Read(ulong va, Span<byte> data)
        {
            // 如果是音频缓冲区，则进行限速
            if (IsAudioBuffer(va, (ulong)data.Length))
            {
                _audioRateLimiter.Wait(data.Length);
            }

            try
            {
                base.Read(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
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
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
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
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
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
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
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
            // 音频缓冲区直接返回连续物理区域
            if (IsAudioBuffer(va, size))
            {
                ulong paStart = GetPhysicalAddressInternal(va);
                return new List<MemoryRange> { new MemoryRange(paStart, size) };
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

        /// <inheritdoc/>
        public bool IsRangeMappedSafe(ulong va, ulong size)
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
            if (!ValidateAddress(va))
            {
                return false;
            }

            return _pageTable.Read<ulong>((va / PageSize) * PteSize) != 0;
        }

        private nuint GetPhysicalAddressInternal(ulong va)
        {
            return (nuint)(PteToPa(_pageTable.Read<ulong>((va / PageSize) * PteSize) & ~(0xffffUL << 48)) + (va & PageMask);
        }

        /// <inheritdoc/>
        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            // TODO
        }

        /// <inheritdoc/>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            // 跳过音频缓冲区的保护操作
            if (IsAudioBuffer(va, size)) return;
            
            AssertValidAddressAndSize(va, size);

            if (guest)
            {
                // Protection is inverted on software pages, since the default value is 0.
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
            // 提升音频线程优先级
            if (id == AudioThreadId)
            {
                try
                {
#if ANDROID
                    // Android 优先级调整
                    Process.SetThreadPriority(Process.MyTid(), (int)ThreadPriority.Highest);
#else
                    // 其他平台的优先级调整
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;
#endif
                }
                catch
                {
                    // 忽略权限错误
                }
            }
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
            // 跳过音频缓冲区的内存跟踪
            if (IsAudioBuffer(va, size)) return;
            
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            // If the memory tracking is coming from the guest, use the tag bits in the page table entry.
            // Otherwise, use the managed page flags.

            if (guest)
            {
                // We emulate guard pages for software memory access. This makes for an easy transition to
                // tracking using host guard pages in future, but also supporting platforms where this is not possible.

                // Write tag includes read protection, since we don't have any read actions that aren't performed before write too.
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
            return (ulong)_backingMemory.GetPointer(pa, PageSize);
        }

        private ulong PteToPa(ulong pte)
        {
            return (ulong)((long)pte - _backingMemory.Pointer.ToInt64());
        }

        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy() => _pageTable.Dispose();

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
            => _backingMemory.GetMemory(pa, size);

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
            => _backingMemory.GetSpan(pa, size);

        protected override nuint TranslateVirtualAddressChecked(ulong va)
            => GetPhysicalAddressInternal(va);

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
            => GetPhysicalAddressInternal(va);
    }
}
