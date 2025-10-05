using Silk.NET.Vulkan;
using System;
using Ryujinx.Common.Logging;
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

        // 添加内存压力状态
        private MemoryPressureState _pressureState;
        private DateTime _lastMemoryCleanup;

        // 将枚举改为 public
        public enum MemoryPressureState
        {
            Normal,
            Moderate,
            High,
            Critical
        }

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
            _pressureState = MemoryPressureState.Normal;
            _lastMemoryCleanup = DateTime.Now;
            
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
            
            // 更智能的内存限制策略 - 基于实际设备内存
            ulong calculatedLimit;
            
            if (totalDeviceMemory > 6UL * 1024 * 1024 * 1024) // 6GB以上
            {
                calculatedLimit = Math.Min(totalDeviceMemory * 3 / 4, 4UL * 1024 * 1024 * 1024); // 最多4GB
            }
            else if (totalDeviceMemory > 4UL * 1024 * 1024 * 1024) // 4GB-6GB
            {
                calculatedLimit = Math.Min(totalDeviceMemory * 70 / 100, 3UL * 1024 * 1024 * 1024); // 最多3GB
            }
            else if (totalDeviceMemory > 2UL * 1024 * 1024 * 1024) // 2GB-4GB
            {
                calculatedLimit = Math.Min(totalDeviceMemory * 75 / 100, 2UL * 1024 * 1024 * 1024); // 最多2GB
            }
            else if (totalDeviceMemory > 1UL * 1024 * 1024 * 1024) // 1GB-2GB
            {
                calculatedLimit = Math.Min(totalDeviceMemory * 70 / 100, 1536UL * 1024 * 1024); // 最多1.5GB
            }
            else // 1GB以下
            {
                calculatedLimit = totalDeviceMemory / 2; // 使用一半
            }
            
            // 确保至少有一定的最小限制
            calculatedLimit = Math.Max(calculatedLimit, 512UL * 1024 * 1024); // 至少512MB
            
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
            // 更新内存压力状态
            UpdateMemoryPressureState();

            // 对于大内存分配，使用特殊处理
            if (requirements.Size > 64 * 1024 * 1024) // 64MB以上
            {
                return AllocateLargeMemory(requirements, flags, isBuffer);
            }

            // 常规内存分配
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private void UpdateMemoryPressureState()
        {
            double usageRatio = (double)_currentUsage / _memoryLimit;
            
            MemoryPressureState newState = usageRatio switch
            {
                < 0.6 => MemoryPressureState.Normal,
                < 0.8 => MemoryPressureState.Moderate,
                < 0.9 => MemoryPressureState.High,
                _ => MemoryPressureState.Critical
            };

            if (newState != _pressureState)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Memory pressure state changed: {_pressureState} -> {newState} (Usage: {usageRatio:P})");
                _pressureState = newState;
            }

            // 在高压状态下定期清理内存
            if (_pressureState >= MemoryPressureState.High && 
                (DateTime.Now - _lastMemoryCleanup).TotalSeconds > 30)
            {
                Logger.Info?.Print(LogClass.Gpu, "Performing periodic memory cleanup due to high memory pressure");
                CompactMemory();
                ForceGarbageCollection();
                _lastMemoryCleanup = DateTime.Now;
            }
        }

        private MemoryAllocation AllocateLargeMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            _largeAllocationAttempts++;
            
            ulong availableMemory = _memoryLimit - _currentUsage;
            double usageRatio = (double)_currentUsage / _memoryLimit;

            Logger.Warning?.Print(LogClass.Gpu, 
                $"Large memory allocation attempt #{_largeAllocationAttempts}: " +
                $"Size=0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB), " +
                $"CurrentUsage=0x{_currentUsage:X} ({_currentUsage / (1024 * 1024)}MB, {usageRatio:P}), " +
                $"Available=0x{availableMemory:X} ({availableMemory / (1024 * 1024)}MB)");

            // 检查内存压力并采取相应措施
            if (_pressureState == MemoryPressureState.Critical && requirements.Size > 128 * 1024 * 1024)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    "Rejecting large allocation due to critical memory pressure");
                return default;
            }

            // 如果请求大小超过可用内存，尝试智能处理
            if (requirements.Size > availableMemory)
            {
                return HandleInsufficientMemory(requirements, flags, isBuffer, availableMemory);
            }

            // 正常分配流程
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private MemoryAllocation HandleInsufficientMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer,
            ulong availableMemory)
        {
            // 策略1: 尝试内存清理
            if (_largeAllocationAttempts <= 2)
            {
                Logger.Info?.Print(LogClass.Gpu, "Attempting memory cleanup for large allocation");
                CompactMemory();
                ForceGarbageCollection();
                
                // 重新计算可用内存
                availableMemory = _memoryLimit - _currentUsage;
                if (requirements.Size <= availableMemory)
                {
                    Logger.Info?.Print(LogClass.Gpu, "Memory cleanup successful, proceeding with allocation");
                    return AllocateRegularMemory(requirements, flags, isBuffer);
                }
            }

            // 策略2: 对于特别大的分配，建议使用分段缓冲区
            if (requirements.Size > 256 * 1024 * 1024)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Consider using segmented buffer for very large allocation: 0x{requirements.Size:X}");
            }

            // 策略3: 在高压状态下允许轻微超限
            if (_pressureState < MemoryPressureState.Critical)
            {
                ulong allowedOvershoot = _memoryLimit / 20; // 5%
                if (_currentUsage + requirements.Size <= _memoryLimit + allowedOvershoot)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Allowing memory allocation slightly above limit: {(_currentUsage + requirements.Size) * 100 / _memoryLimit}% of limit");
                    return AllocateRegularMemory(requirements, flags, isBuffer);
                }
            }

            // 策略4: 返回失败，让上层代码处理
            Logger.Error?.Print(LogClass.Gpu, 
                $"Insufficient memory for allocation: Requested=0x{requirements.Size:X}, Available=0x{availableMemory:X}");
            return default;
        }

        private MemoryAllocation AllocateRegularMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            // 检查内存限制
            if (_currentUsage + requirements.Size > _memoryLimit)
            {
                // 在适度压力下允许小幅度超限
                if (_pressureState <= MemoryPressureState.Moderate)
                {
                    ulong allowedOvershoot = _memoryLimit / 50; // 2%
                    if (_currentUsage + requirements.Size <= _memoryLimit + allowedOvershoot)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Allowing small allocation slightly above limit: {(_currentUsage + requirements.Size) * 100 / _memoryLimit}% of limit");
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Memory allocation would exceed limit: Current=0x{_currentUsage:X}, Requested=0x{requirements.Size:X}, Limit=0x{_memoryLimit:X}");
                        return default;
                    }
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Memory allocation would exceed limit: Current=0x{_currentUsage:X}, Requested=0x{requirements.Size:X}, Limit=0x{_memoryLimit:X}");
                    return default;
                }
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
                            $"New usage: 0x{_currentUsage:X} ({_currentUsage / (1024 * 1024)}MB, {(double)_currentUsage / _memoryLimit:P})");
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
            
            // 更新内存压力状态
            UpdateMemoryPressureState();
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
        public (ulong totalAllocated, ulong totalFreed, ulong currentUsage, ulong peakUsage, ulong memoryLimit, MemoryPressureState pressureState) GetMemoryStatistics()
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

            return (totalAllocated, totalFreed, _currentUsage, _peakUsage, _memoryLimit, _pressureState);
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
