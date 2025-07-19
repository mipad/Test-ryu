using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Pools;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ryujinx.Memory.Tracking
{
    /// <summary>
    /// 管理虚拟/物理内存块的内存跟踪
    /// </summary>
    public class MemoryTracking
    {
        private readonly IVirtualMemoryManager _memoryManager;
        private readonly InvalidAccessHandler _invalidAccessHandler;

        private readonly NonOverlappingRangeList<VirtualRegion> _virtualRegions;
        private readonly NonOverlappingRangeList<VirtualRegion> _guestVirtualRegions;

        private readonly int _pageSize;
        private readonly bool _singleByteGuestTracking;

        /// <summary>
        /// 用于保护区域-句柄层次结构的锁
        /// </summary>
        internal object TrackingLock = new();

        // === 新增诊断字段 ===
        private static readonly Stopwatch _diagnosticTimer = Stopwatch.StartNew();
        private long _lastNullAccessTime;
        private int _nullAccessCount;
        // ===================
        
        /// <summary>
        /// 为给定的"物理"内存块创建新的跟踪结构
        /// </summary>
        public MemoryTracking(
            IVirtualMemoryManager memoryManager,
            int pageSize,
            InvalidAccessHandler invalidAccessHandler = null,
            bool singleByteGuestTracking = false)
        {
            _memoryManager = memoryManager;
            _pageSize = pageSize;
            _invalidAccessHandler = invalidAccessHandler;
            _singleByteGuestTracking = singleByteGuestTracking;

            _virtualRegions = new NonOverlappingRangeList<VirtualRegion>();
            _guestVirtualRegions = new NonOverlappingRangeList<VirtualRegion>();
        }

        /// <summary>
        /// 将地址和大小按页大小对齐
        /// </summary>
        private (ulong address, ulong size) PageAlign(ulong address, ulong size)
        {
            ulong pageMask = (ulong)_pageSize - 1;
            ulong rA = address & ~pageMask;
            ulong rS = ((address + size + pageMask) & ~pageMask) - rA;
            return (rA, rS);
        }

        /// <summary>
        /// 通知虚拟区域已被映射
        /// </summary>
        public void Map(ulong va, ulong size)
        {
            lock (TrackingLock)
            {
                ref var overlaps = ref ThreadStaticArray<VirtualRegion>.Get();

                for (int type = 0; type < 2; type++)
                {
                    NonOverlappingRangeList<VirtualRegion> regions = type == 0 ? _virtualRegions : _guestVirtualRegions;
                    int count = regions.FindOverlapsNonOverlapping(va, size, ref overlaps);

                    for (int i = 0; i < count; i++)
                    {
                        VirtualRegion region = overlaps[i];
                        bool remapped = _memoryManager.IsRangeMapped(region.Address, region.Size);
                        if (remapped)
                        {
                            region.SignalMappingChanged(true);
                        }
                        region.UpdateProtection();
                    }
                }
            }
        }

        /// <summary>
        /// 通知虚拟区域将被取消映射
        /// </summary>
        public void Unmap(ulong va, ulong size)
        {
            lock (TrackingLock)
            {
                ref var overlaps = ref ThreadStaticArray<VirtualRegion>.Get();

                for (int type = 0; type < 2; type++)
                {
                    NonOverlappingRangeList<VirtualRegion> regions = type == 0 ? _virtualRegions : _guestVirtualRegions;
                    int count = regions.FindOverlapsNonOverlapping(va, size, ref overlaps);

                    for (int i = 0; i < count; i++)
                    {
                        VirtualRegion region = overlaps[i];
                        region.SignalMappingChanged(false);
                    }
                }
            }
        }

        /// <summary>
        /// 获取安全的未对齐访问区域
        /// </summary>
        internal (ulong newAddress, ulong newSize) GetUnalignedSafeRegion(ulong address, ulong size)
        {
            if (_singleByteGuestTracking)
            {
                return (address - (ulong)_pageSize, size + (ulong)_pageSize);
            }
            return (address, size);
        }

        /// <summary>
        /// 获取句柄覆盖的虚拟区域列表
        /// </summary>
        internal List<VirtualRegion> GetVirtualRegionsForHandle(ulong va, ulong size, bool guest)
        {
            List<VirtualRegion> result = new();
            NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
            regions.GetOrAddRegions(result, va, size, (va, size) => new VirtualRegion(this, va, size, guest));
            return result;
        }

        /// <summary>
        /// 从范围列表中移除虚拟区域
        /// </summary>
        internal void RemoveVirtual(VirtualRegion region)
        {
            if (region.Guest)
            {
                _guestVirtualRegions.Remove(region);
            }
            else
            {
                _virtualRegions.Remove(region);
            }
        }

        /// <summary>
        /// 开始粒度跟踪
        /// </summary>
        public MultiRegionHandle BeginGranularTracking(
            ulong address, 
            ulong size, 
            IEnumerable<IRegionHandle> handles, 
            ulong granularity, 
            int id, 
            RegionFlags flags = RegionFlags.None)
        {
            return new MultiRegionHandle(this, address, size, handles, granularity, id, flags);
        }

        /// <summary>
        /// 开始智能粒度跟踪
        /// </summary>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            (address, size) = PageAlign(address, size);
            return new SmartMultiRegionHandle(this, address, size, granularity, id);
        }

        /// <summary>
        /// 开始内存跟踪
        /// </summary>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags = RegionFlags.None)
        {
            var (paAddress, paSize) = PageAlign(address, size);

            lock (TrackingLock)
            {
                bool mapped = _memoryManager.IsRangeMapped(address, size);
                return new RegionHandle(this, paAddress, paSize, address, size, id, flags, mapped);
            }
        }

        /// <summary>
        /// 开始位图内存跟踪
        /// </summary>
        internal RegionHandle BeginTrackingBitmap(
            ulong address, 
            ulong size, 
            ConcurrentBitmap bitmap, 
            int bit, 
            int id, 
            RegionFlags flags = RegionFlags.None)
        {
            var (paAddress, paSize) = PageAlign(address, size);

            lock (TrackingLock)
            {
                bool mapped = _memoryManager.IsRangeMapped(address, size);
                return new RegionHandle(this, paAddress, paSize, address, size, bitmap, bit, id, flags, mapped);
            }
        }

        /// <summary>
        /// 虚拟内存事件处理
        /// </summary>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            return VirtualMemoryEvent(address, size, write, precise: false, exemptId: null, guest: true);
        }

        /// <summary>
        /// 虚拟内存事件处理（精确版本）
        /// </summary>
        public bool VirtualMemoryEvent(
            ulong address, 
            ulong size, 
            bool write, 
            bool precise, 
            int? exemptId = null, 
            bool guest = false)
        {
            if (address == 0)
            {
                long currentTime = _diagnosticTimer.ElapsedMilliseconds;
                long timeSinceLast = currentTime - _lastNullAccessTime;
                _lastNullAccessTime = currentTime;
                _nullAccessCount++;
                
                Logger.Warning?.Print(LogClass.Cpu,
                    $"[NULL ACCESS] Addr=0x0, Size=0x{size:X}, Write={write}, " +
                    $"Precise={precise}, Guest={guest}, Count={_nullAccessCount}, " +
                    $"TimeSinceLast={timeSinceLast}ms");
                
                #if DEBUG
                Logger.Debug?.Print(LogClass.Cpu,
                    $"Null Access Stack:\n{Environment.StackTrace}");
                #endif
            }
            
            bool shouldThrow = false;

            lock (TrackingLock)
            {
                ref var overlaps = ref ThreadStaticArray<VirtualRegion>.Get();
                NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
                int count = regions.FindOverlapsNonOverlapping(address, size, ref overlaps);

                if (count == 0 && !precise)
                {
                    if (_memoryManager.IsRangeMapped(address, size))
                    {
                        _memoryManager.TrackingReprotect(
                            address & ~(ulong)(_pageSize - 1), 
                            (ulong)_pageSize, 
                            MemoryPermission.ReadAndWrite, 
                            guest);
                        return true;
                    }
                    else
                    {
                        string regionInfo = GetRegionInfoNearAddress(address);
                        Logger.Error?.Print(LogClass.Cpu, 
                            $"Invalid memory access at 0x{address:X}, size 0x{size:X}, write: {write}\n" +
                            $"Nearby Regions:\n{regionInfo}");
                        
                        if (_nullAccessCount > 0)
                        {
                            Logger.Error?.Print(LogClass.Cpu, 
                                $"Null access pattern: {_nullAccessCount} times in last {_diagnosticTimer.ElapsedMilliseconds}ms");
                        }
                        
                        shouldThrow = true;
                    }
                }
                else
                {
                    if (guest && _singleByteGuestTracking)
                    {
                        size += (ulong)_pageSize;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        VirtualRegion region = overlaps[i];
                        if (precise)
                        {
                            region.SignalPrecise(address, size, write, exemptId);
                        }
                        else
                        {
                            region.Signal(address, size, write, exemptId);
                        }
                    }
                }
            }

            if (shouldThrow)
            {
                if (address == 0)
                {
                    Logger.Error?.Print(LogClass.Cpu, 
                        "CRITICAL: NULL pointer access detected! Forcing debugger attach.");
                    
                    #if DEBUG
                    if (Debugger.IsAttached)
                    {
                        Debugger.Break();
                    }
                    else
                    {
                        Debugger.Launch();
                    }
                    #endif
                }
                
                _invalidAccessHandler?.Invoke(address);
                throw new InvalidMemoryRegionException($"Access violation at 0x{address:X}");
            }

            return true;
        }

        /// <summary>
        /// 获取地址附近的区域信息（诊断用）
        /// </summary>
        private string GetRegionInfoNearAddress(ulong address)
        {
            const int range = 0x10000;
            List<string> regionInfos = new();
            
            ulong start = address > range ? address - range : 0;
            ulong end = address + range;
            
            lock (TrackingLock)
            {
                ref var overlaps = ref ThreadStaticArray<VirtualRegion>.Get();
                
                int count = _virtualRegions.FindOverlapsNonOverlapping(start, end - start, ref overlaps);
                for (int i = 0; i < count; i++)
                {
                    var region = overlaps[i];
                    regionInfos.Add($"Virt: 0x{region.Address:X}-0x{region.EndAddress:X} " +
                                     $"({region.Size / 1024}KB)");
                }
                
                count = _guestVirtualRegions.FindOverlapsNonOverlapping(start, end - start, ref overlaps);
                for (int i = 0; i < count; i++)
                {
                    var region = overlaps[i];
                    regionInfos.Add($"Guest: 0x{region.Address:X}-0x{region.EndAddress:X} " +
                                    $"({region.Size / 1024}KB)");
                }
            }
            
            return regionInfos.Count > 0 
                ? string.Join("\n", regionInfos) 
                : "No mapped regions found near address";
        }

        /// <summary>
        /// 重新保护虚拟区域
        /// </summary>
        internal void ProtectVirtualRegion(VirtualRegion region, MemoryPermission permission, bool guest)
        {
            _memoryManager.TrackingReprotect(region.Address, region.Size, permission, guest);
        }

        /// <summary>
        /// 获取当前跟踪的虚拟区域数量
        /// </summary>
        public int GetRegionCount()
        {
            lock (TrackingLock)
            {
                return _virtualRegions.Count;
            }
        }
        
        /// <summary>
        /// 获取空指针访问统计信息（诊断用）
        /// </summary>
        public string GetNullAccessDiagnostics()
        {
            return $"Null accesses: {_nullAccessCount}, " +
                   $"Last: {_diagnosticTimer.ElapsedMilliseconds - _lastNullAccessTime}ms ago";
        }
    }
}
