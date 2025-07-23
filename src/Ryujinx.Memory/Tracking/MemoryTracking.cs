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
    /// Manages memory tracking for virtual/physical memory blocks.
    /// </summary>
    public class MemoryTracking
    {
        private readonly IVirtualMemoryManager _memoryManager;
        private readonly InvalidAccessHandler _invalidAccessHandler;

        // New: Audio buffer detection delegate
        private readonly System.Func<ulong, ulong, bool> _isAudioRegion;

        // The following fields need to be accessed within a lock
        private readonly NonOverlappingRangeList<VirtualRegion> _virtualRegions;
        private readonly NonOverlappingRangeList<VirtualRegion> _guestVirtualRegions;

        private readonly int _pageSize;
        private readonly bool _singleByteGuestTracking;
        private readonly bool _ignoreNullAccess; // New: NULL access ignore flag

        /// <summary>
        /// Lock used to protect the region-handle hierarchy.
        /// </summary>
        internal object TrackingLock = new();

        // === Diagnostic fields ===
        private static readonly Stopwatch _diagnosticTimer = Stopwatch.StartNew();
        private long _lastNullAccessTime;
        private int _nullAccessCount;
        // ========================

        /// <summary>
        /// Creates a new tracking structure for the given "physical" memory block.
        /// </summary>
        public MemoryTracking(
            IVirtualMemoryManager memoryManager,
            int pageSize,
            InvalidAccessHandler invalidAccessHandler = null,
            bool singleByteGuestTracking = false,
            System.Func<ulong, ulong, bool> isAudioRegion = null,
            bool ignoreNullAccess = false) // New parameter
        {
            _memoryManager = memoryManager;
            _pageSize = pageSize;
            _invalidAccessHandler = invalidAccessHandler;
            _singleByteGuestTracking = singleByteGuestTracking;
            _isAudioRegion = isAudioRegion;
            _ignoreNullAccess = ignoreNullAccess;

            _virtualRegions = new NonOverlappingRangeList<VirtualRegion>();
            _guestVirtualRegions = new NonOverlappingRangeList<VirtualRegion>();
        }

        /// <summary>
        /// Aligns an address and size to the page size.
        /// </summary>
        private (ulong address, ulong size) PageAlign(ulong address, ulong size)
        {
            ulong pageMask = (ulong)_pageSize - 1;
            ulong rA = address & ~pageMask;
            ulong rS = ((address + size + pageMask) & ~pageMask) - rA;
            return (rA, rS);
        }

        /// <summary>
        /// Indicates that a virtual region has been mapped.
        /// </summary>
        public void Map(ulong va, ulong size)
        {
            // Skip mapping processing for audio regions
            if (_isAudioRegion != null && _isAudioRegion(va, size)) return;
            
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
        /// Indicates that a virtual region is about to be unmapped.
        /// </summary>
        public void Unmap(ulong va, ulong size)
        {
            // Skip unmapping processing for audio regions
            if (_isAudioRegion != null && _isAudioRegion(va, size)) return;
            
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
        /// Gets a safe unaligned access region.
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
        /// Gets a list of virtual regions that a handle covers.
        /// </summary>
        internal List<VirtualRegion> GetVirtualRegionsForHandle(ulong va, ulong size, bool guest)
        {
            List<VirtualRegion> result = new();
            NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
            regions.GetOrAddRegions(result, va, size, (va, size) => new VirtualRegion(this, va, size, guest));
            return result;
        }

        /// <summary>
        /// Remove a virtual region from the range list.
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
        /// Begin granular tracking for the given region.
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
        /// Begin smart granular tracking for the given region.
        /// </summary>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            (address, size) = PageAlign(address, size);
            return new SmartMultiRegionHandle(this, address, size, granularity, id);
        }

        /// <summary>
        /// Begin memory tracking for the given region.
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
        /// Begin bitmap memory tracking for the given region.
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
        /// Handle a virtual memory event (read/write).
        /// </summary>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            return VirtualMemoryEvent(address, size, write, precise: false, exemptId: null, guest: true);
        }

        /// <summary>
        /// Handle a virtual memory event (read/write) with precise information.
        /// </summary>
        public bool VirtualMemoryEvent(
            ulong address, 
            ulong size, 
            bool write, 
            bool precise, 
            int? exemptId = null, 
            bool guest = false)
        {
            // NULL access diagnostics
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

                // New: NULL access special handling
                if (_ignoreNullAccess)
                {
                    Logger.Warning?.Print(LogClass.Cpu, "NULL access ignored by configuration");
                    return true;
                }
            }
            
            // Skip audio region memory event processing
            if (_isAudioRegion != null && _isAudioRegion(address, size))
            {
                // Enable audio skip logging (uncomment for debugging)
                // Logger.Trace?.Print(LogClass.Cpu, 
                //    $"Skipping audio region access: VA=0x{address:X}, Size={size}");
                return true;
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
                        // Enhanced error logging
                        string regionInfo = GetRegionInfoNearAddress(address);
                        Logger.Error?.Print(LogClass.Cpu, 
                            $"Invalid memory access at 0x{address:X}, size 0x{size:X}, write: {write}\n" +
                            $"Nearby Regions:\n{regionInfo}");
                        
                        // Log access pattern history
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
                // NULL pointer special handling
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
        /// Gets information about regions near an address (for diagnostics).
        /// </summary>
        private string GetRegionInfoNearAddress(ulong address)
        {
            const int range = 0x10000; // Search nearby 64KB range
            List<string> regionInfos = new();
            
            ulong start = address > range ? address - range : 0;
            ulong end = address + range;
            
            lock (TrackingLock)
            {
                ref var overlaps = ref ThreadStaticArray<VirtualRegion>.Get();
                
                // Check normal virtual regions
                int count = _virtualRegions.FindOverlapsNonOverlapping(start, end - start, ref overlaps);
                for (int i = 0; i < count; i++)
                {
                    var region = overlaps[i];
                    regionInfos.Add($"Virt: 0x{region.Address:X}-0x{region.EndAddress:X} " +
                                     $"({region.Size / 1024}KB)");
                }
                
                // Check guest virtual regions
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
        /// Reprotect a virtual region.
        /// </summary>
        internal void ProtectVirtualRegion(VirtualRegion region, MemoryPermission permission, bool guest)
        {
            _memoryManager.TrackingReprotect(region.Address, region.Size, permission, guest);
        }

        /// <summary>
        /// Gets the count of currently tracked virtual regions.
        /// </summary>
        public int GetRegionCount()
        {
            lock (TrackingLock)
            {
                return _virtualRegions.Count;
            }
        }
        
        /// <summary>
        /// Gets NULL access diagnostics information.
        /// </summary>
        public string GetNullAccessDiagnostics()
        {
            return $"Null accesses: {_nullAccessCount}, " +
                   $"Last: {_diagnosticTimer.ElapsedMilliseconds - _lastNullAccessTime}ms ago";
        }
    }
}
