using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024;
        private const ulong LargeAllocationThreshold = 256 * 1024 * 1024; // 256MB
        private const ulong ChunkSize = 64 * 1024 * 1024; // 64MB 分块大小

        private readonly Vk _api;
        private readonly VulkanPhysicalDevice _physicalDevice;
        private readonly Device _device;
        private readonly List<MemoryAllocatorBlockList> _blockLists;
        private readonly int _blockAlignment;
        private readonly ReaderWriterLockSlim _lock;
        
        // 内存压力回调
        public event Action<ulong, ulong> OnMemoryPressure;

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                return default;
            }

            bool map = flags.HasFlag(MemoryPropertyFlags.HostVisibleBit);
            
            // 检查可用显存
            if (!CanAllocate(requirements.Size, memoryTypeIndex))
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Insufficient memory! Requested: {FormatSize(requirements.Size)}, " +
                    $"Available: {FormatSize(GetAvailableMemory(memoryTypeIndex))}");
                
                // 触发内存压力回调
                OnMemoryPressure?.Invoke(requirements.Size, 0);
                Thread.Sleep(100); // 给释放操作一点时间
            }

            // 大内存分配警告
            if (requirements.Size > LargeAllocationThreshold)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Allocating large buffer: {FormatSize(requirements.Size)} " +
                    $"(Type: {memoryTypeIndex}, Flags: {flags})");
                
                // 使用分块策略分配大内存
                return AllocateChunked(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
            }

            // 普通分配
            return AllocateWithRetry(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
        }

        private MemoryAllocation AllocateChunked(
            int memoryTypeIndex, 
            ulong size, 
            ulong alignment, 
            bool map, 
            bool isBuffer)
        {
            // 计算需要多少块
            ulong chunks = (size + ChunkSize - 1) / ChunkSize;
            List<MemoryAllocation> allocations = new((int)chunks);

            try
            {
                // 分配每个块
                for (ulong i = 0; i < chunks; i++)
                {
                    ulong chunkSize = (i == chunks - 1) ? 
                        size - (i * ChunkSize) : 
                        ChunkSize;
                        
                    var allocation = AllocateWithRetry(
                        memoryTypeIndex, 
                        chunkSize, 
                        alignment, 
                        map, 
                        isBuffer);
                        
                    if (allocation.Memory.Handle == 0)
                    {
                        throw new OutOfMemoryException($"Failed to allocate chunk {i}/{chunks}");
                    }
                    
                    allocations.Add(allocation);
                }

                // 创建组合分配
                return new ChunkedMemoryAllocation(allocations);
            }
            catch
            {
                // 释放已分配的部分
                foreach (var alloc in allocations)
                {
                    alloc.Dispose();
                }
                throw;
            }
        }

        private MemoryAllocation AllocateWithRetry(
            int memoryTypeIndex, 
            ulong size, 
            ulong alignment, 
            bool map, 
            bool isBuffer)
        {
            const int MaxRetries = 3;
            int attempt = 0;

            while (attempt++ < MaxRetries)
            {
                var allocation = Allocate(memoryTypeIndex, size, alignment, map, isBuffer);
                
                if (allocation.Memory.Handle != 0)
                {
                    return allocation;
                }

                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation failed for {FormatSize(size)} (attempt {attempt}/{MaxRetries})");
                
                // 触发内存压力回调
                OnMemoryPressure?.Invoke(size, (ulong)attempt);
                
                // 指数退避等待
                Thread.Sleep(50 * attempt);
            }

            Logger.Error?.Print(LogClass.Gpu, 
                $"Memory allocation failed after {MaxRetries} attempts: {FormatSize(size)}");
            return default;
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

        // 检查是否有足够显存
        private bool CanAllocate(ulong size, int memoryTypeIndex)
        {
            var stats = GetMemoryStatistics(memoryTypeIndex);
            
            // 保留10%作为安全余量
            ulong safetyMargin = stats.TotalMemory / 10;
            return stats.FreeMemory > (size + safetyMargin);
        }

        // 获取可用显存
        private ulong GetAvailableMemory(int memoryTypeIndex)
        {
            var stats = GetMemoryStatistics(memoryTypeIndex);
            return stats.FreeMemory;
        }

        // 获取内存统计信息
        private MemoryStatistics GetMemoryStatistics(int memoryTypeIndex)
        {
            _lock.EnterReadLock();
            try
            {
                var stats = new MemoryStatistics();
                
                foreach (var bl in _blockLists.Where(b => b.MemoryTypeIndex == memoryTypeIndex))
                {
                    var blStats = bl.GetStatistics();
                    stats.TotalMemory += blStats.TotalMemory;
                    stats.FreeMemory += blStats.FreeMemory;
                }
                
                return stats;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        // 格式化内存大小
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
        }
    }

    // 组合内存分配（用于分块策略）
    class ChunkedMemoryAllocation : MemoryAllocation
    {
        private readonly List<MemoryAllocation> _chunks;

        public ChunkedMemoryAllocation(List<MemoryAllocation> chunks) 
            : base(default, 0, 0, 0, false)
        {
            _chunks = chunks;
        }

        public override void Dispose()
        {
            foreach (var chunk in _chunks)
            {
                chunk.Dispose();
            }
        }
    }

    // 内存统计信息
    struct MemoryStatistics
    {
        public ulong TotalMemory;
        public ulong FreeMemory;
    }
}
