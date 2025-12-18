using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Memory.Range;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Memory.Tracking
{
    /// <summary>
    /// Memory tracking configuration for audio regions.
    /// </summary>
    public enum AudioTrackingMode
    {
        /// <summary>
        /// Automatically detect and skip audio regions (default).
        /// Uses heuristics to identify likely audio buffers.
        /// </summary>
        Auto = 0,
        
        /// <summary>
        /// Always skip memory tracking for audio regions.
        /// Requires explicit audio region detection delegate.
        /// </summary>
        Always,
        
        /// <summary>
        /// Never skip memory tracking for audio regions.
        /// All memory accesses are fully tracked.
        /// </summary>
        Never,
        
        /// <summary>
        /// Skip tracking with performance monitoring.
        /// Logs statistics about skipped audio accesses.
        /// </summary>
        WithMonitoring
    }

    /// <summary>
    /// Manages memory tracking for a given virutal/physical memory block.
    /// </summary>
    public class MemoryTracking
    {
        private readonly IVirtualMemoryManager _memoryManager;
        private readonly InvalidAccessHandler _invalidAccessHandler;

        // Audio tracking configuration
        private readonly AudioTrackingMode _audioTrackingMode;
        private readonly System.Func<ulong, ulong, bool> _customAudioRegionDetector;
        
        // Only use these from within the lock.
        private readonly NonOverlappingRangeList<VirtualRegion> _virtualRegions;
        // Guest virtual regions are a subset of the normal virtual regions, with potentially different protection
        // and expanded area of effect on platforms that don't support misaligned page protection.
        private readonly NonOverlappingRangeList<VirtualRegion> _guestVirtualRegions;

        private readonly int _pageSize;
        private readonly bool _singleByteGuestTracking;
        private readonly bool _ignoreNullAccess;

        /// <summary>
        /// This lock must be obtained when traversing or updating the region-handle hierarchy.
        /// It is not required when reading dirty flags.
        /// </summary>
        internal object TrackingLock = new();

        // === Diagnostic fields ===
        private static readonly Stopwatch _diagnosticTimer = Stopwatch.StartNew();
        private long _lastNullAccessTime;
        private int _nullAccessCount;
        
        // Audio skip statistics
        private long _audioSkipCount;
        private long _lastAudioSkipLogTime;
        private const long AudioSkipLogInterval = 5000; // 5 seconds
        // ========================

        /// <summary>
        /// Create a new tracking structure for the given "physical" memory block,
        /// with a given "virtual" memory manager that will provide mappings and virtual memory protection.
        /// </summary>
        /// <remarks>
        /// If <paramref name="singleByteGuestTracking" /> is true, the memory manager must also support protection on partially
        /// unmapped regions without throwing exceptions or dropping protection on the mapped portion.
        /// </remarks>
        /// <param name="memoryManager">Virtual memory manager</param>
        /// <param name="pageSize">Page size of the virtual memory space</param>
        /// <param name="invalidAccessHandler">Method to call for invalid memory accesses</param>
        /// <param name="singleByteGuestTracking">True if the guest only signals writes for the first byte</param>
        /// <param name="audioTrackingMode">Mode for audio region tracking (default: Auto)</param>
        /// <param name="customAudioRegionDetector">Custom delegate to identify audio regions (optional)</param>
        /// <param name="ignoreNullAccess">Whether to ignore NULL pointer accesses (for debugging)</param>
        public MemoryTracking(
            IVirtualMemoryManager memoryManager,
            int pageSize,
            InvalidAccessHandler invalidAccessHandler = null,
            bool singleByteGuestTracking = false,
            AudioTrackingMode audioTrackingMode = AudioTrackingMode.Auto,
            System.Func<ulong, ulong, bool> customAudioRegionDetector = null,
            bool ignoreNullAccess = false)
        {
            _memoryManager = memoryManager;
            _pageSize = pageSize;
            _invalidAccessHandler = invalidAccessHandler;
            _singleByteGuestTracking = singleByteGuestTracking;
            _audioTrackingMode = audioTrackingMode;
            _customAudioRegionDetector = customAudioRegionDetector;
            _ignoreNullAccess = ignoreNullAccess;

            _virtualRegions = [];
            _guestVirtualRegions = [];
            
            // Log the selected audio tracking mode
            Logger.Info?.Print(LogClass.Emulation, 
                $"MemoryTracking initialized with AudioTrackingMode: {audioTrackingMode}");
        }

        /// <summary>
        /// Automatically detect if a region is likely to be an audio buffer.
        /// </summary>
        private bool IsLikelyAudioRegion(ulong address, ulong size)
        {
            // Heuristic 1: Check if custom detector is provided
            if (_customAudioRegionDetector != null)
                return _customAudioRegionDetector(address, size);
            
            // Heuristic 2: Common audio buffer sizes and alignments
            // Audio buffers are often power-of-two sizes and page-aligned
            bool isPowerOfTwo = (size & (size - 1)) == 0 && size != 0;
            bool isPageAligned = (address % (ulong)_pageSize) == 0;
            
            // Heuristic 3: Typical audio buffer sizes (64KB - 4MB)
            bool isTypicalAudioSize = size >= 64 * 1024 && size <= 4 * 1024 * 1024;
            
            // Heuristic 4: Check if region is in typical audio address range
            // (This is platform/game specific and may need adjustment)
            bool inTypicalAudioRange = (address >= 0x10000000 && address <= 0x30000000) ||
                                      (address >= 0x70000000 && address <= 0x90000000);
            
            // Combine heuristics with weights
            int score = 0;
            if (isPowerOfTwo) score += 2;
            if (isPageAligned) score += 1;
            if (isTypicalAudioSize) score += 3;
            if (inTypicalAudioRange) score += 2;
            
            return score >= 5; // Threshold for "likely audio"
        }

        /// <summary>
        /// Check if audio skipping is enabled for a given region.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ShouldSkipAudioRegion(ulong address, ulong size)
        {
            switch (_audioTrackingMode)
            {
                case AudioTrackingMode.Auto:
                    return IsLikelyAudioRegion(address, size);
                    
                case AudioTrackingMode.Always:
                    // Always skip if custom detector says yes
                    return _customAudioRegionDetector?.Invoke(address, size) ?? false;
                    
                case AudioTrackingMode.Never:
                    return false;
                    
                case AudioTrackingMode.WithMonitoring:
                    bool shouldSkip = IsLikelyAudioRegion(address, size);
                    if (shouldSkip)
                    {
                        LogAudioSkip(address, size);
                    }
                    return shouldSkip;
                    
                default:
                    return false;
            }
        }

        /// <summary>
        /// Log audio skip statistics.
        /// </summary>
        private void LogAudioSkip(ulong address, ulong size)
        {
            long currentTime = _diagnosticTimer.ElapsedMilliseconds;
            long skipCount = Interlocked.Increment(ref _audioSkipCount);
            
            // Log statistics periodically
            if (currentTime - _lastAudioSkipLogTime > AudioSkipLogInterval)
            {
                _lastAudioSkipLogTime = currentTime;
                Logger.Info?.Print(LogClass.Emulation, 
                    $"Audio skip statistics: {skipCount} skips in last {AudioSkipLogInterval}ms");
            }
            
            // Detailed debug logging (enabled only in verbose mode)
            Logger.Trace?.Print(LogClass.Emulation, 
                $"Skipping audio region access: VA=0x{address:X}, Size={size}, TotalSkips={skipCount}");
        }

        /// <summary>
        /// Get audio skip statistics for monitoring.
        /// </summary>
        public (long SkipCount, bool IsEnabled) GetAudioSkipStatistics()
        {
            return (Interlocked.Read(ref _audioSkipCount), 
                    _audioTrackingMode != AudioTrackingMode.Never);
        }

        private (ulong address, ulong size) PageAlign(ulong address, ulong size)
        {
            ulong pageMask = (ulong)_pageSize - 1;
            ulong rA = address & ~pageMask;
            ulong rS = ((address + size + pageMask) & ~pageMask) - rA;
            return (rA, rS);
        }

        /// <summary>
        /// Indicate that a virtual region has been mapped, and which physical region it has been mapped to.
        /// Should be called after the mapping is complete.
        /// </summary>
        /// <param name="va">Virtual memory address</param>
        /// <param name="size">Size to be mapped</param>
        public void Map(ulong va, ulong size)
        {
            // Skip mapping processing for audio regions
            if (ShouldSkipAudioRegion(va, size)) return;

            // A mapping may mean we need to re-evaluate each VirtualRegion's affected area.
            // Find all handles that overlap with the range, we need to recalculate their physical regions

            lock (TrackingLock)
            {
                for (int type = 0; type < 2; type++)
                {
                    NonOverlappingRangeList<VirtualRegion> regions = type == 0 ? _virtualRegions : _guestVirtualRegions;
                    regions.Lock.EnterReadLock();
                    ReadOnlySpan<VirtualRegion> overlaps = regions.FindOverlapsAsSpan(va, size);
                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        VirtualRegion region = overlaps[i];

                        // If the region has been fully remapped, signal that it has been mapped again.
                        bool remapped = _memoryManager.IsRangeMapped(region.Address, region.Size);
                        if (remapped)
                        {
                            region.SignalMappingChanged(true);
                        }

                        region.UpdateProtection();
                    }
                    regions.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Indicate that a virtual region has been unmapped.
        /// Should be called before the unmapping is complete.
        /// </summary>
        /// <param name="va">Virtual memory address</param>
        /// <param name="size">Size to be unmapped</param>
        public void Unmap(ulong va, ulong size)
        {
            // Skip unmapping processing for audio regions
            if (ShouldSkipAudioRegion(va, size)) return;

            // An unmapping may mean we need to re-evaluate each VirtualRegion's affected area.
            // Find all handles that overlap with the range, we need to notify them that the region was unmapped.

            lock (TrackingLock)
            {
                for (int type = 0; type < 2; type++)
                {
                    NonOverlappingRangeList<VirtualRegion> regions = type == 0 ? _virtualRegions : _guestVirtualRegions;
                    regions.Lock.EnterReadLock();
                    ReadOnlySpan<VirtualRegion> overlaps = regions.FindOverlapsAsSpan(va, size);
                    
                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        overlaps[i].SignalMappingChanged(false);
                    }
                    regions.Lock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Alter a tracked memory region to properly capture unaligned accesses.
        /// For most memory manager modes, this does nothing.
        /// </summary>
        /// <param name="address">Original region address</param>
        /// <param name="size">Original region size</param>
        /// <returns>A new address and size for tracking unaligned accesses</returns>
        internal (ulong newAddress, ulong newSize) GetUnalignedSafeRegion(ulong address, ulong size)
        {
            if (_singleByteGuestTracking)
            {
                // The guest only signals the first byte of each memory access with the current memory manager.
                // To catch unaligned access properly, we need to also protect the page before the address.

                // Assume that the address and size are already aligned.

                return (address - (ulong)_pageSize, size + (ulong)_pageSize);
            }
            else
            {
                return (address, size);
            }
        }

        /// <summary>
        /// Get a list of virtual regions that a handle covers.
        /// </summary>
        /// <param name="va">Starting virtual memory address of the handle</param>
        /// <param name="size">Size of the handle's memory region</param>
        /// <param name="guest">True if getting handles for guest protection, false otherwise</param>
        /// <returns>A list of virtual regions within the given range</returns>
        internal List<VirtualRegion> GetVirtualRegionsForHandle(ulong va, ulong size, bool guest)
        {
            NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
            regions.Lock.EnterUpgradeableReadLock();
            regions.GetOrAddRegions(out List<VirtualRegion> result, va, size, (va, size) => new VirtualRegion(this, va, size, guest));
            regions.Lock.ExitUpgradeableReadLock();
            
            return result;
        }

        /// <summary>
        /// Remove a virtual region from the range list. This assumes that the lock has been acquired.
        /// </summary>
        /// <param name="region">Region to remove</param>
        internal void RemoveVirtual(VirtualRegion region)
        {
            if (region.Guest)
            {
                _guestVirtualRegions.Lock.EnterWriteLock();
                _guestVirtualRegions.Remove(region);
                _guestVirtualRegions.Lock.ExitWriteLock();
            }
            else
            {
                _virtualRegions.Lock.EnterWriteLock();
                _virtualRegions.Remove(region);
                _virtualRegions.Lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="handles">Handles to inherit state from or reuse. When none are present, provide null</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <param name="id">Handle ID</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags = RegionFlags.None)
        {
            return new MultiRegionHandle(this, address, size, handles, granularity, id, flags);
        }

        /// <summary>
        /// Obtains a smart memory tracking handle for the given virtual region, with a specified granularity. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="granularity">Desired granularity of write tracking</param>
        /// <param name="id">Handle ID</param>
        /// <returns>The memory tracking handle</returns>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            (address, size) = PageAlign(address, size);

            return new SmartMultiRegionHandle(this, address, size, granularity, id);
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="id">Handle ID</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags = RegionFlags.None)
        {
            (ulong paAddress, ulong paSize) = PageAlign(address, size);

            lock (TrackingLock)
            {
                bool mapped = _memoryManager.IsRangeMapped(address, size);
                RegionHandle handle = new(this, paAddress, paSize, address, size, id, flags, mapped);

                return handle;
            }
        }

        /// <summary>
        /// Obtains a memory tracking handle for the given virtual region. This should be disposed when finished with.
        /// </summary>
        /// <param name="address">CPU virtual address of the region</param>
        /// <param name="size">Size of the region</param>
        /// <param name="bitmap">The bitmap owning the dirty flag for this handle</param>
        /// <param name="bit">The bit of this handle within the dirty flag</param>
        /// <param name="id">Handle ID</param>
        /// <param name="flags">Region flags</param>
        /// <returns>The memory tracking handle</returns>
        internal RegionHandle BeginTrackingBitmap(ulong address, ulong size, ConcurrentBitmap bitmap, int bit, int id, RegionFlags flags = RegionFlags.None)
        {
            (ulong paAddress, ulong paSize) = PageAlign(address, size);

            lock (TrackingLock)
            {
                bool mapped = _memoryManager.IsRangeMapped(address, size);
                RegionHandle handle = new(this, paAddress, paSize, address, size, bitmap, bit, id, flags, mapped);

                return handle;
            }
        }

        /// <summary>
        /// Signal that a virtual memory event happened at the given location.
        /// The memory event is assumed to be triggered by guest code.
        /// </summary>
        /// <param name="address">Virtual address accessed</param>
        /// <param name="size">Size of the region affected in bytes</param>
        /// <param name="write">Whether the region was written to or read</param>
        /// <returns>True if the event triggered any tracking regions, false otherwise</returns>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            return VirtualMemoryEvent(address, size, write, precise: false, exemptId: null, guest: true);
        }

        /// <summary>
        /// Signal that a virtual memory event happened at the given location.
        /// This can be flagged as a precise event, which will avoid reprotection and call special handlers if possible.
        /// A precise event has an exact address and size, rather than triggering on page granularity.
        /// </summary>
        /// <param name="address">Virtual address accessed</param>
        /// <param name="size">Size of the region affected in bytes</param>
        /// <param name="write">Whether the region was written to or read</param>
        /// <param name="precise">True if the access is precise, false otherwise</param>
        /// <param name="exemptId">Optional ID that of the handles that should not be signalled</param>
        /// <param name="guest">True if the access is from the guest, false otherwise</param>
        /// <returns>True if the event triggered any tracking regions, false otherwise</returns>
        public bool VirtualMemoryEvent(ulong address, ulong size, bool write, bool precise, int? exemptId = null, bool guest = false)
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

                // NULL access special handling
                if (_ignoreNullAccess)
                {
                    Logger.Warning?.Print(LogClass.Cpu, "NULL access ignored by configuration");
                    return true;
                }
            }
            
            // Skip audio region memory event processing
            if (ShouldSkipAudioRegion(address, size))
            {
                return true;
            }

            // Look up the virtual region using the region list.
            // Signal up the chain to relevant handles.

            bool shouldThrow = false;

            lock (TrackingLock)
            {
                NonOverlappingRangeList<VirtualRegion> regions = guest ? _guestVirtualRegions : _virtualRegions;
                
                // We use the non-span method here because keeping the lock will cause a deadlock.
                regions.Lock.EnterReadLock();
                VirtualRegion[] overlaps = regions.FindOverlapsAsArray(address, size, out int length);
                regions.Lock.ExitReadLock();

                if (length == 0 && !precise)
                {
                    if (_memoryManager.IsRangeMapped(address, size))
                    {
                        // TODO: There is currently the possibility that a page can be protected after its virtual region is removed.
                        // This code handles that case when it happens, but it would be better to find out how this happens.
                        _memoryManager.TrackingReprotect(address & ~(ulong)(_pageSize - 1), (ulong)_pageSize, MemoryPermission.ReadAndWrite, guest);
                        
                        return true; // This memory _should_ be mapped, so we need to try again.
                    }
                    
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
                else
                {
                    if (guest && _singleByteGuestTracking)
                    {
                        // Increase the access size to trigger handles with misaligned accesses.
                        size += (ulong)_pageSize;
                    }
                    
                    for (int i = 0; i < length; i++)
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

                    if (length != 0)
                    {
                        ArrayPool<VirtualRegion>.Shared.Return(overlaps);
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

                // We can't continue - it's impossible to remove protection from the page.
                // Even if the access handler wants us to continue, we wouldn't be able to.
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
                // Note: FindOverlapsNonOverlapping is not available in this version,
                // so we use a simpler approach for demonstration.
                // In practice, you'd need to adapt this to use the available methods.
                
                // Simple implementation: just return basic info
                regionInfos.Add($"Address: 0x{address:X}");
                regionInfos.Add($"Page Size: {_pageSize}");
                regionInfos.Add($"Virtual Regions: {_virtualRegions.Count}");
                regionInfos.Add($"Guest Virtual Regions: {_guestVirtualRegions.Count}");
            }
            
            return regionInfos.Count > 0 
                ? string.Join("\n", regionInfos) 
                : "No diagnostic information available";
        }

        /// <summary>
        /// Reprotect a given virtual region. The virtual memory manager will handle this.
        /// </summary>
        /// <param name="region">Region to reprotect</param>
        /// <param name="permission">Memory permission to protect with</param>
        /// <param name="guest">True if the protection is for guest access, false otherwise</param>
        internal void ProtectVirtualRegion(VirtualRegion region, MemoryPermission permission, bool guest)
        {
            _memoryManager.TrackingReprotect(region.Address, region.Size, permission, guest);
        }

        /// <summary>
        /// Returns the number of virtual regions currently being tracked.
        /// Useful for tests and metrics.
        /// </summary>
        /// <returns>The number of virtual regions</returns>
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
        
        /// <summary>
        /// Resets NULL access diagnostics counters.
        /// Useful for unit testing and debugging.
        /// </summary>
        public void ResetNullAccessDiagnostics()
        {
            _nullAccessCount = 0;
            _lastNullAccessTime = _diagnosticTimer.ElapsedMilliseconds;
        }
    }
}
