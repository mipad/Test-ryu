using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024;
        private const ulong LargeAllocationThreshold = 256 * 1024 * 1024; // 256MB
        private const int MaxRetries = 5;
        private const int BaseRetryDelayMs = 100;
        private const int MaxConcurrentLargeAllocations = 1;
        private const float MemorySafetyMarginFactor = 0.05f; // 20% safety margin

        private readonly Vk _api;
        private readonly VulkanPhysicalDevice _physicalDevice;
        private readonly Device _device;
        private readonly List<MemoryAllocatorBlockList> _blockLists;
        private readonly int _blockAlignment;
        private readonly ReaderWriterLockSlim _lock;
        
        // 内存压力回调
        public event Action<ulong, ulong> OnMemoryPressure;
        
        // 大内存分配信号量
        private readonly SemaphoreSlim _largeAllocSemaphore = new(MaxConcurrentLargeAllocations, MaxConcurrentLargeAllocations);

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"MemoryAllocator initialized: " +
                $"BlockAlignment={FormatSize((ulong)_blockAlignment)}, " +
                $"LargeThreshold={FormatSize(LargeAllocationThreshold)}");
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"No suitable memory type found! " +
                    $"TypeBits: {requirements.MemoryTypeBits}, Flags: {flags}");
                return default;
            }

            bool map = flags.HasFlag(MemoryPropertyFlags.HostVisibleBit);
            ulong size = requirements.Size;
            
            // 大内存分配特殊处理
            if (size > LargeAllocationThreshold)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Allocating large buffer: {FormatSize(size)} " +
                    $"(Type: {memoryTypeIndex}, Flags: {flags})");
                
                // 获取大内存分配信号量（限制并发）
                if (!_largeAllocSemaphore.Wait(TimeSpan.FromSeconds(30)))
                {
                    Logger.Error?.Print(LogClass.Gpu, 
                        "Timeout waiting for large allocation semaphore!");
                    return default;
                }
                
                try
                {
                    return AllocateWithRetry(memoryTypeIndex, size, requirements.Alignment, map, isBuffer, true);
                }
                finally
                {
                    _largeAllocSemaphore.Release();
                }
            }
            
            return AllocateWithRetry(memoryTypeIndex, size, requirements.Alignment, map, isBuffer, false);
        }

        private MemoryAllocation AllocateWithRetry(
            int memoryTypeIndex, 
            ulong size, 
            ulong alignment, 
            bool map, 
            bool isBuffer,
            bool isLargeAllocation)
        {
            int attempt = 0;
            var sw = Stopwatch.StartNew();
            string sizeStr = FormatSize(size);

            while (attempt < MaxRetries)
            {
                attempt++;
                
                // 检查内存可用性（大分配需要额外安全余量）
                if (isLargeAllocation && !IsMemoryAvailable(memoryTypeIndex, size, MemorySafetyMarginFactor))
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Memory insufficient for large allocation: {sizeStr} " +
                        $"(Type: {memoryTypeIndex}, Attempt: {attempt}/{MaxRetries})");
                    
                    // 触发内存压力事件
                    OnMemoryPressure?.Invoke(size, (ulong)attempt);
                    
                    // 指数退避等待（100ms, 200ms, 400ms...）
                    int waitTime = BaseRetryDelayMs * (1 << (attempt - 1));
                    Logger.Info?.Print(LogClass.Gpu, $"Waiting {waitTime}ms for memory release...");
                    Thread.Sleep(waitTime);
                    continue;
                }

                var allocation = Allocate(memoryTypeIndex, size, alignment, map, isBuffer);
                
                if (allocation.Memory.Handle != 0)
                {
                    if (isLargeAllocation || attempt > 1)
                    {
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Allocation succeeded after {attempt} attempts " +
                            $"(Time: {sw.ElapsedMilliseconds}ms): {sizeStr}");
                    }
                    return allocation;
                }

                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation failed: {sizeStr} " +
                    $"(Type: {memoryTypeIndex}, Attempt: {attempt}/{MaxRetries})");
                
                // 触发内存压力事件
                OnMemoryPressure?.Invoke(size, (ulong)attempt);
                
                // 指数退避等待
                int delay = BaseRetryDelayMs * (1 << (attempt - 1));
                Thread.Sleep(delay);
            }

            // 最终失败处理
            Logger.Error?.Print(LogClass.Gpu, 
                $"Memory allocation FAILED after {MaxRetries} attempts: {sizeStr} " +
                $"(Total time: {sw.ElapsedMilliseconds}ms)");
            
            LogMemoryStatus(memoryTypeIndex);
            return default;
        }

        private bool IsMemoryAvailable(int memoryTypeIndex, ulong requiredSize, float safetyMarginFactor)
        {
            try
            {
                // 获取内存堆信息
                uint heapIndex = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypes[memoryTypeIndex].HeapIndex;
                ulong heapSize = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[heapIndex].Size;
                
                // 估算已使用内存（简单实现，实际需要更精确统计）
                ulong estimatedUsed = EstimateUsedMemory(memoryTypeIndex);
                ulong freeMemory = heapSize - estimatedUsed;
                
                // 计算安全余量
                ulong safetyMargin = (ulong)(heapSize * safetyMarginFactor);
                ulong requiredTotal = requiredSize + safetyMargin;
                
                bool available = freeMemory >= requiredTotal;
                
                if (!available)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Memory check: Required={FormatSize(requiredTotal)} " +
                        $"(Size: {FormatSize(requiredSize)} + Margin: {FormatSize(safetyMargin)}), " +
                        $"Free={FormatSize(freeMemory)}/{FormatSize(heapSize)}");
                }
                
                return available;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory check failed: {ex.Message}");
                return true; // 出错时允许尝试分配
            }
        }

        private ulong EstimateUsedMemory(int memoryTypeIndex)
        {
            // 简化实现 - 实际应跟踪每个内存类型的使用量
            // 此处返回0表示未知，后续需要完善
            return 0;
        }

        private void LogMemoryStatus(int memoryTypeIndex)
        {
            try
            {
                uint heapIndex = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypes[memoryTypeIndex].HeapIndex;
                ulong heapSize = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[heapIndex].Size;
                
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory Heap Status: " +
                    $"TypeIndex={memoryTypeIndex}, " +
                    $"HeapIndex={heapIndex}, " +
                    $"HeapSize={FormatSize(heapSize)}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory status logging failed: {ex.Message}");
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

        // 辅助方法：格式化内存大小
        private static string FormatSize(ulong size)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = size;
            int unitIndex = 0;

            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return $"{value:0.##} {units[unitIndex]}";
        }

        public void Dispose()
        {
            for (int i = 0; i < _blockLists.Count; i++)
            {
                _blockLists[i].Dispose();
            }
            
            _largeAllocSemaphore.Dispose();
            _lock.Dispose();
            
            Logger.Info?.Print(LogClass.Gpu, "MemoryAllocator disposed");
        }
    }
}
