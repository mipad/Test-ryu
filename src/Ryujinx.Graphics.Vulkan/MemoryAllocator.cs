using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024;
        private const ulong LargeAllocationThreshold = 256 * 1024 * 1024; // 256MB

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
            const int MaxRetries = 3;
            int attempt = 0;

            while (attempt++ < MaxRetries)
            {
                var allocation = Allocate(memoryTypeIndex, size, alignment, map, isBuffer);
                
                // 使用Handle检查分配是否有效
                if (allocation.Memory.Handle != 0)
                {
                    return allocation;
                }

                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation failed for {FormatSize(size)} (attempt {attempt}/{MaxRetries})");
                
                // 触发内存清理回调
                OnMemoryPressure?.Invoke(size, (ulong)attempt);
                
                // 等待资源释放
                Thread.Sleep(50 * attempt);
            }

            Logger.Error?.Print(LogClass.Gpu, 
                $"Memory allocation failed after {MaxRetries} attempts: {FormatSize(size)}");
            throw new VulkanMemoryException(VkResult.ErrorOutOfDeviceMemory, size, true);
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
        }
    }

    // 自定义内存异常
    class VulkanMemoryException : OutOfMemoryException
    {
        public VkResult Result { get; }
        public ulong AllocationSize { get; }
        public bool IsCritical { get; }

        public VulkanMemoryException(VkResult result, ulong allocationSize, bool isCritical) 
            : base($"Vulkan memory allocation failed: {result}, size={allocationSize}")
        {
            Result = result;
            AllocationSize = allocationSize;
            IsCritical = isCritical;
        }
    }
}
