using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
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

        // 添加内存限制
        private readonly ulong _memoryLimit;
        private ulong _currentUsage;
        private ulong _peakUsage;

        // 添加大分配计数
        private int _largeAllocationAttempts;
        private const int MaxLargeAllocationAttempts = 3;

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
            
            // 设置更合理的内存限制
            _memoryLimit = CalculateMemoryLimit(physicalDevice);
            _currentUsage = 0;
            _peakUsage = 0;
            _largeAllocationAttempts = 0;
            
            Logger.Info?.Print(LogClass.Gpu, $"Memory allocator initialized with limit: 0x{_memoryLimit:X} ({_memoryLimit / (1024 * 1024)}MB)");
        }

        private ulong CalculateMemoryLimit(VulkanPhysicalDevice physicalDevice)
        {
            ulong totalDeviceMemory = 0;
            ulong largestHeap = 0;
            
            for (int i = 0; i < physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeapCount; i++)
            {
                var heap = physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    totalDeviceMemory += heap.Size;
                    if (heap.Size > largestHeap)
                    {
                        largestHeap = heap.Size;
                    }
                }
            }
            
            // 更智能的内存限制策略
            ulong calculatedLimit;
            
            if (totalDeviceMemory > 2UL * 1024 * 1024 * 1024) // 2GB以上
            {
                calculatedLimit = Math.Min(totalDeviceMemory * 3 / 4, 1536UL * 1024 * 1024); // 最多1.5GB
            }
            else if (totalDeviceMemory > 1UL * 1024 * 1024 * 1024) // 1GB-2GB
            {
                calculatedLimit = Math.Min(totalDeviceMemory * 2 / 3, 1024UL * 1024 * 1024); // 最多1GB
            }
            else // 1GB以下
            {
                calculatedLimit = totalDeviceMemory / 2; // 使用一半
            }
            
            // 确保至少有一定的最小限制
            calculatedLimit = Math.Max(calculatedLimit, 256UL * 1024 * 1024); // 至少256MB
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"Memory stats - Total: 0x{totalDeviceMemory:X} ({totalDeviceMemory / (1024 * 1024)}MB), " +
                $"Largest Heap: 0x{largestHeap:X} ({largestHeap / (1024 * 1024)}MB), " +
                $"Limit: 0x{calculatedLimit:X} ({calculatedLimit / (1024 * 1024)}MB)");
            
            return calculatedLimit;
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            // 对于大内存分配，使用特殊处理
            if (requirements.Size > 64 * 1024 * 1024) // 64MB以上
            {
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
            _largeAllocationAttempts++;
            
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Large memory allocation attempt #{_largeAllocationAttempts}: Size=0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB), " +
                $"CurrentUsage=0x{_currentUsage:X} ({_currentUsage / (1024 * 1024)}MB), " +
                $"Limit=0x{_memoryLimit:X} ({_memoryLimit / (1024 * 1024)}MB)");

            // 检查是否超过限制但还有空间
            bool wouldExceedLimit = _currentUsage + requirements.Size > _memoryLimit;
            ulong availableMemory = _memoryLimit - _currentUsage;

            if (wouldExceedLimit && availableMemory > 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Large allocation would exceed limit, but there's available memory: 0x{availableMemory:X} ({availableMemory / (1024 * 1024)}MB)");
                
                // 如果请求的大小远大于可用内存，尝试使用可用内存
                if (requirements.Size > availableMemory * 2)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Requested size is much larger than available memory. Considering reduced allocation.");
                    
                    // 这里可以返回失败，让上层代码处理大小调整
                    // 或者尝试分配可用内存
                }
            }

            // 对于大分配，放宽限制检查
            if (wouldExceedLimit)
            {
                // 允许超过限制10%以内，但要记录警告
                ulong allowedOvershoot = _memoryLimit / 10; // 10%
                if (_currentUsage + requirements.Size <= _memoryLimit + allowedOvershoot)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Allowing memory allocation slightly above limit: {(_currentUsage + requirements.Size) * 100 / _memoryLimit}% of limit");
                }
                else if (_largeAllocationAttempts <= MaxLargeAllocationAttempts)
                {
                    // 如果是前几次大分配尝试，尝试强制垃圾回收和内存压缩
                    Logger.Info?.Print(LogClass.Gpu, "Attempting memory cleanup for large allocation");
                    CompactMemory();
                    ForceGarbageCollection();
                    
                    // 重新检查
                    if (_currentUsage + requirements.Size > _memoryLimit + allowedOvershoot)
                    {
                        Logger.Error?.Print(LogClass.Gpu, 
                            $"Memory allocation would significantly exceed limit even after cleanup. Requested: 0x{requirements.Size:X}");
                        return default;
                    }
                }
                else
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        $"Memory allocation would significantly exceed limit after {_largeAllocationAttempts} attempts. Requested: 0x{requirements.Size:X}");
                    return default;
                }
            }

            // 正常分配流程
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private MemoryAllocation AllocateRegularMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            // 检查内存限制
            if (_currentUsage + requirements.Size > _memoryLimit)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation would exceed limit: Current=0x{_currentUsage:X}, Requested=0x{requirements.Size:X}, Limit=0x{_memoryLimit:X}");
                return default;
            }

            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"No suitable memory type found for requirements: TypeBits=0x{requirements.MemoryTypeBits:X}, Flags={flags}");
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
                    
                    // 记录大分配
                    if (requirements.Size > 64 * 1024 * 1024)
                    {
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Large memory allocation successful: 0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB), " +
                            $"New usage: 0x{_currentUsage:X} ({_currentUsage / (1024 * 1024)}MB)");
                    }
                }
                return allocation;
            }
            catch (VulkanException ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory allocation failed: Size=0x{requirements.Size:X}, TypeIndex={memoryTypeIndex}, Error={ex.Message}");
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
        public (ulong totalAllocated, ulong totalFreed, ulong currentUsage, ulong peakUsage, ulong memoryLimit) GetMemoryStatistics()
        {
            ulong totalAllocated = 0;
            ulong totalFreed = 0;
            ulong currentUsage = 0;

            foreach (var blockList in _blockLists)
            {
                var stats = blockList.GetMemoryStatistics();
                totalAllocated += stats.allocated;
                totalFreed += stats.freed;
                currentUsage += stats.currentUsage;
            }

            return (totalAllocated, totalFreed, _currentUsage, _peakUsage, _memoryLimit);
        }

        // 添加内存压缩方法（尝试合并空闲块）
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
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // 重置大分配计数
        public void ResetLargeAllocationCount()
        {
            _largeAllocationAttempts = 0;
        }

        public void Dispose()
        {
            for (int i = 0; i < _blockLists.Count; i++)
            {
                _blockLists[i].Dispose();
            }
        }
    }
}
