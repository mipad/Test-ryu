using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024;

        private readonly Vk _api;
        private readonly VulkanPhysicalDevice _physicalDevice;
        private readonly Device _device;
        private readonly List<MemoryAllocatorBlockList> _blockLists;
        private readonly int _blockAlignment;
        private readonly ReaderWriterLockSlim _lock;

        // 内存统计和监控
        private ulong _currentUsage;
        private ulong _peakUsage;
        private ulong _totalDeviceMemory;
        private ulong _largestHeap;

        // 分配统计
        private int _totalAllocations;
        private int _failedAllocations;
        private int _largeAllocationAttempts;

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
            
            // 初始化内存统计
            InitializeMemoryStats(physicalDevice);
            _currentUsage = 0;
            _peakUsage = 0;
            _totalAllocations = 0;
            _failedAllocations = 0;
            _largeAllocationAttempts = 0;
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"Memory allocator initialized - Total VRAM: {_totalDeviceMemory / (1024 * 1024)}MB, " +
                $"Largest Heap: {_largestHeap / (1024 * 1024)}MB");
        }

        private void InitializeMemoryStats(VulkanPhysicalDevice physicalDevice)
        {
            _totalDeviceMemory = 0;
            _largestHeap = 0;
            
            for (int i = 0; i < physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeapCount; i++)
            {
                var heap = physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    _totalDeviceMemory += heap.Size;
                    if (heap.Size > _largestHeap)
                    {
                        _largestHeap = heap.Size;
                    }
                }
            }
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            _totalAllocations++;

            // 对于大内存分配，使用特殊处理
            if (requirements.Size > 64 * 1024 * 1024) // 64MB以上
            {
                _largeAllocationAttempts++;
                return AllocateLargeMemory(requirements, flags, isBuffer);
            }

            // 常规内存分配
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private MemoryAllocation AllocateLargeMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            ulong requestedSize = requirements.Size;
            double usagePercentage = (_currentUsage + requestedSize) * 100.0 / _totalDeviceMemory;

            Logger.Warning?.Print(LogClass.Gpu, 
                $"Large memory allocation #{_largeAllocationAttempts}: " +
                $"Size={requestedSize / (1024 * 1024)}MB, " +
                $"Current={_currentUsage / (1024 * 1024)}MB, " +
                $"Total={_totalDeviceMemory / (1024 * 1024)}MB, " +
                $"Usage={usagePercentage:F1}%");

            // 检查是否接近内存限制
            if (usagePercentage > 85.0) // 超过85%使用率
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"High memory usage detected: {usagePercentage:F1}%. Attempting memory optimization...");
                
                // 尝试内存优化
                if (!TryOptimizeMemoryForLargeAllocation(requestedSize))
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Cannot allocate {requestedSize / (1024 * 1024)}MB. Memory usage would be {usagePercentage:F1}%");
                    _failedAllocations++;
                    return default;
                }
            }

            // 正常分配流程
            var allocation = AllocateRegularMemory(requirements, flags, isBuffer);
            if (allocation.Memory.Handle == 0)
            {
                _failedAllocations++;
                
                // 分配失败，尝试最后的手段
                Logger.Warning?.Print(LogClass.Gpu, "Large allocation failed, attempting emergency memory cleanup");
                PerformEmergencyMemoryCleanup();
                
                // 重试一次
                allocation = AllocateRegularMemory(requirements, flags, isBuffer);
            }

            return allocation;
        }

        private bool TryOptimizeMemoryForLargeAllocation(ulong requestedSize)
        {
            // 策略1: 强制垃圾回收
            Logger.Info?.Print(LogClass.Gpu, "Forcing garbage collection for large allocation");
            ForceGarbageCollection();
            
            // 策略2: 压缩内存
            CompactMemory();
            
            // 策略3: 检查当前使用情况
            ulong availableMemory = _totalDeviceMemory - _currentUsage;
            if (availableMemory < requestedSize)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Insufficient memory: Available={availableMemory / (1024 * 1024)}MB, " +
                    $"Requested={requestedSize / (1024 * 1024)}MB");
                
                // 如果请求的大小超过可用内存，但设备总内存足够，可能是碎片问题
                // 在这种情况下，我们仍然尝试分配，让系统处理
                if (requestedSize < _totalDeviceMemory * 0.7) // 请求小于总内存70%
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        "Requested size is less than 70% of total memory. Allowing allocation attempt despite fragmentation.");
                    return true;
                }
                
                return false;
            }
            
            return true;
        }

        private void PerformEmergencyMemoryCleanup()
        {
            Logger.Warning?.Print(LogClass.Gpu, "Performing emergency memory cleanup");
            
            // 强制完整的垃圾回收
            for (int i = 0; i < 3; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
            }
            
            // 如果有缓存清理机制，在这里调用
            // ClearAllCaches();
            
            Logger.Info?.Print(LogClass.Gpu, "Emergency memory cleanup completed");
        }

        private MemoryAllocation AllocateRegularMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"No suitable memory type found for requirements: TypeBits=0x{requirements.MemoryTypeBits:X}, Flags={flags}");
                _failedAllocations++;
                return default;
            }

            bool map = flags.HasFlag(MemoryPropertyFlags.HostVisibleBit);
            
            try
            {
                var allocation = Allocate(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
                if (allocation.Memory.Handle != 0)
                {
                    _currentUsage += requirements.Size;
                    _peakUsage = Math.Max(_peakUsage, _currentUsage);
                    
                    // 记录内存使用情况
                    double usagePercent = _currentUsage * 100.0 / _totalDeviceMemory;
                    if (usagePercent > 80.0)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"High memory usage: {_currentUsage / (1024 * 1024)}MB / {_totalDeviceMemory / (1024 * 1024)}MB ({usagePercent:F1}%)");
                    }
                }
                else
                {
                    _failedAllocations++;
                }
                
                return allocation;
            }
            catch (VulkanException ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory allocation failed: Size=0x{requirements.Size:X}, TypeIndex={memoryTypeIndex}, Error={ex.Message}");
                _failedAllocations++;
                return default;
            }
        }

        // 添加方法来释放内存并更新使用量
        internal void NotifyMemoryFreed(ulong size)
        {
            if (size > _currentUsage)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory free size larger than current usage: Free=0x{size:X}, Current=0x{_currentUsage:X}");
                _currentUsage = 0;
            }
            else
            {
                _currentUsage -= size;
                
                // 记录内存释放后的使用情况
                double usagePercent = _currentUsage * 100.0 / _totalDeviceMemory;
                if (usagePercent < 50.0 && _peakUsage > _totalDeviceMemory * 0.8)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Memory usage normalized: {_currentUsage / (1024 * 1024)}MB / {_totalDeviceMemory / (1024 * 1024)}MB ({usagePercent:F1}%)");
                }
            }
        }

        private MemoryAllocation Allocate(int memoryTypeIndex, ulong size, ulong alignment, bool map, bool isBuffer)
        {
            _lock.EnterReadLock();

            try
            {
                for (int i = 0; i < _blockLists.Count; i++)
                {
                    var bl = _blockLists[i];
                    if (bl.MemoryTypeIndex == memoryTypeIndex && bl.ForBuffer == isBuffer)
                    {
                        return bl.Allocate(size, alignment, map);
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            _lock.EnterWriteLock();

            try
            {
                var newBl = new MemoryAllocatorBlockList(_api, _device, memoryTypeIndex, _blockAlignment, isBuffer);
                _blockLists.Add(newBl);

                return newBl.Allocate(size, alignment, map);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        internal int FindSuitableMemoryTypeIndex(
            uint memoryTypeBits,
            MemoryPropertyFlags flags)
        {
            // 首先尝试完全匹配
            for (int i = 0; i < _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypeCount; i++)
            {
                var type = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypes[i];

                if ((memoryTypeBits & (1 << i)) != 0)
                {
                    if (type.PropertyFlags.HasFlag(flags))
                    {
                        return i;
                    }
                }
            }

            // 如果没有完全匹配，尝试寻找包含所需标志的内存类型
            for (int i = 0; i < _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypeCount; i++)
            {
                var type = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypes[i];

                if ((memoryTypeBits & (1 << i)) != 0)
                {
                    if ((type.PropertyFlags & flags) == flags)
                    {
                        return i;
                    }
                }
            }

            // 最后，尝试任何可用的内存类型（作为最后的手段）
            for (int i = 0; i < _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypeCount; i++)
            {
                if ((memoryTypeBits & (1 << i)) != 0)
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool IsDeviceMemoryShared(VulkanPhysicalDevice physicalDevice)
        {
            for (int i = 0; i < physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeapCount; i++)
            {
                if (!physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[i].Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit))
                {
                    return false;
                }
            }

            return true;
        }

        public ulong GetFreeMemory()
        {
            ulong freeMemory = 0;
            foreach (var blockList in _blockLists)
            {
                freeMemory += blockList.GetFreeMemory();
            }
            return freeMemory;
        }

        // 添加获取最大可用块大小的方法
        public ulong GetLargestAvailableBlock()
        {
            ulong largest = 0;
            foreach (var blockList in _blockLists)
            {
                ulong blockListLargest = blockList.GetLargestFreeBlock();
                if (blockListLargest > largest)
                {
                    largest = blockListLargest;
                }
            }
            return largest;
        }

        // 添加内存统计方法
        public MemoryStatistics GetMemoryStatistics()
        {
            ulong totalAllocated = 0;
            ulong totalFreed = 0;
            ulong currentBlockUsage = 0;

            foreach (var blockList in _blockLists)
            {
                var stats = blockList.GetMemoryStatistics();
                totalAllocated += stats.allocated;
                totalFreed += stats.freed;
                currentBlockUsage += stats.currentUsage;
            }

            return new MemoryStatistics
            {
                TotalAllocated = totalAllocated,
                TotalFreed = totalFreed,
                CurrentUsage = _currentUsage,
                PeakUsage = _peakUsage,
                TotalDeviceMemory = _totalDeviceMemory,
                LargestHeap = _largestHeap,
                TotalAllocations = _totalAllocations,
                FailedAllocations = _failedAllocations,
                LargeAllocationAttempts = _largeAllocationAttempts,
                UsagePercentage = _currentUsage * 100.0 / _totalDeviceMemory
            };
        }

        // 内存统计结构
        public struct MemoryStatistics
        {
            public ulong TotalAllocated;
            public ulong TotalFreed;
            public ulong CurrentUsage;
            public ulong PeakUsage;
            public ulong TotalDeviceMemory;
            public ulong LargestHeap;
            public int TotalAllocations;
            public int FailedAllocations;
            public int LargeAllocationAttempts;
            public double UsagePercentage;
        }

        // 添加内存压缩方法
        public void CompactMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                Logger.Info?.Print(LogClass.Gpu, "Memory compaction requested");
                // 这里可以实现内存压缩逻辑
                // 目前只是记录调用
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        // 强制垃圾回收
        private void ForceGarbageCollection()
        {
            Logger.Info?.Print(LogClass.Gpu, "Forcing garbage collection");
            
            // 使用更激进的GC策略
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            // 如果有未完成的任务，等待一下
            System.Threading.Thread.Sleep(10);
        }

        // 重置统计
        public void ResetStatistics()
        {
            _totalAllocations = 0;
            _failedAllocations = 0;
            _largeAllocationAttempts = 0;
        }

        public void Dispose()
        {
            // 输出最终内存统计
            var stats = GetMemoryStatistics();
            Logger.Info?.Print(LogClass.Gpu, 
                $"Memory allocator disposed - Peak usage: {stats.PeakUsage / (1024 * 1024)}MB / {stats.TotalDeviceMemory / (1024 * 1024)}MB " +
                $"({stats.UsagePercentage:F1}%), Failed allocations: {stats.FailedAllocations}");

            for (int i = 0; i < _blockLists.Count; i++)
            {
                _blockLists[i].Dispose();
            }
        }
    }
}
