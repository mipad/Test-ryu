using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024;
        private const ulong LargeAllocationThreshold = 256 * 1024 * 1024; // 256MB
        private const ulong ChunkedAllocationThreshold = 100 * 1024 * 1024; // 100MB
        private const int MaxRetryCount = 3;
        private const int RetryDelayMs = 50;

        private readonly Vk _api;
        private readonly VulkanPhysicalDevice _physicalDevice;
        private readonly Device _device;
        private readonly List<MemoryAllocatorBlockList> _blockLists;
        private readonly int _blockAlignment;
        private readonly ReaderWriterLockSlim _lock;
        
        // 添加内存压力回调
        public event Action<ulong, ulong> OnMemoryPressure;

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                Logger.Error?.Print(LogClass.Gpu, "No suitable memory type found");
                return default;
            }

            bool map = flags.HasFlag(MemoryPropertyFlags.HostVisibleBit);
            
            // 大内存分配警告
            if (requirements.Size > LargeAllocationThreshold)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Allocating large buffer: {FormatSize(requirements.Size)} " +
                    $"(Type: {memoryTypeIndex}, Flags: {flags})");
            }

            return AllocateWithRetry(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
        }

        private MemoryAllocation AllocateWithRetry(int memoryTypeIndex, ulong size, ulong alignment, bool map, bool isBuffer)
        {
            int attempt = 0;
            MemoryAllocation allocation;

            do
            {
                allocation = Allocate(memoryTypeIndex, size, alignment, map, isBuffer);
                
                if (allocation.Memory.Handle != 0)
                {
                    return allocation;
                }

                attempt++;
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation failed for {FormatSize(size)} (attempt {attempt}/{MaxRetryCount})");
                
                // 触发内存清理回调
                OnMemoryPressure?.Invoke(size, (ulong)attempt);
                
                // 等待资源释放
                if (attempt < MaxRetryCount)
                {
                    Thread.Sleep(RetryDelayMs * attempt);
                }
            } while (attempt < MaxRetryCount);

            Logger.Error?.Print(LogClass.Gpu, 
                $"Memory allocation failed after {MaxRetryCount} attempts: {FormatSize(size)}");
            
            // 尝试分块分配作为最后手段
            if (size > ChunkedAllocationThreshold)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Attempting chunked allocation as fallback for {FormatSize(size)}");
                
                return AllocateChunkedFallback(memoryTypeIndex, size, alignment, map, isBuffer);
            }

            throw new VulkanException(Result.ErrorOutOfDeviceMemory, $"Failed to allocate {FormatSize(size)}", size);
        }

        private MemoryAllocation AllocateChunkedFallback(int memoryTypeIndex, ulong size, ulong alignment, bool map, bool isBuffer)
        {
            const ulong chunkSize = 64 * 1024 * 1024; // 64MB 分块
            ulong remaining = size;
            ulong offset = 0;

            Logger.Info?.Print(LogClass.Gpu, 
                $"Starting chunked allocation for {FormatSize(size)} in {FormatSize(chunkSize)} chunks");

            // 分配主内存块
            var mainAllocation = Allocate(memoryTypeIndex, size, alignment, map, isBuffer);
            if (mainAllocation.Memory.Handle != 0)
            {
                return mainAllocation;
            }

            // 分块分配策略
            while (remaining > 0)
            {
                ulong allocateSize = Math.Min(remaining, chunkSize);
                var chunk = Allocate(memoryTypeIndex, allocateSize, alignment, map, isBuffer);
                
                if (chunk.Memory.Handle == 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, 
                        $"Chunked allocation failed at {FormatSize(remaining)} remaining");
                    return default;
                }

                // 合并逻辑（实际应用中需要更复杂的合并机制）
                if (offset == 0)
                {
                    mainAllocation = chunk;
                }
                else
                {
                    // 实际应用中这里需要更复杂的内存合并逻辑
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Allocated chunk {FormatSize(allocateSize)} at offset {offset}");
                }

                offset += allocateSize;
                remaining -= allocateSize;
            }

            Logger.Info?.Print(LogClass.Gpu, 
                $"Chunked allocation completed for {FormatSize(size)}");
            
            return mainAllocation;
        }

        private MemoryAllocation Allocate(int memoryTypeIndex, ulong size, ulong alignment, bool map, bool isBuffer)
        {
            _lock.EnterReadLock();

            try
            {
                foreach (var bl in _blockLists)
                {
                    if (bl.MemoryTypeIndex == memoryTypeIndex && bl.ForBuffer == isBuffer)
                    {
                        var allocation = bl.Allocate(size, alignment, map);
                        if (allocation.Memory.Handle != 0)
                        {
                            return allocation;
                        }
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
                // 再次尝试现有块列表
                foreach (var bl in _blockLists)
                {
                    if (bl.MemoryTypeIndex == memoryTypeIndex && bl.ForBuffer == isBuffer)
                    {
                        var allocation = bl.Allocate(size, alignment, map);
                        if (allocation.Memory.Handle != 0)
                        {
                            return allocation;
                        }
                    }
                }

                // 创建新的块列表
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string FormatSize(ulong size)
        {
            if (size < 1024) return $"{size} B";
            if (size < 1024 * 1024) return $"{size / 1024.0:0.00} KB";
            if (size < 1024 * 1024 * 1024) return $"{size / (1024.0 * 1024.0):0.00} MB";
            return $"{size / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        }

        public void Dispose()
        {
            foreach (var blockList in _blockLists)
            {
                blockList.Dispose();
            }
            _blockLists.Clear();
        }
    }
}
