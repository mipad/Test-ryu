using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Common.Logging;

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
        private readonly VulkanRenderer _renderer; // 新增：用于访问资源缓存

        public MemoryAllocator(
            Vk api, 
            VulkanPhysicalDevice physicalDevice, 
            Device device,
            VulkanRenderer renderer) // 修改：添加renderer参数
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _renderer = renderer; // 存储renderer引用
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
            return Allocate(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
        }

        private MemoryAllocation Allocate(int memoryTypeIndex, ulong size, ulong alignment, bool map, bool isBuffer)
        {
            MemoryAllocation allocation = default;
            bool cleanedUp = true;

            // 记录分配失败日志（包括清理状态）
            if (!allocation.IsValid)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation failed! Size: {size} bytes, TypeIndex: {memoryTypeIndex}, CleanupAttempted: {cleanedUp}");
            }

            return allocation;
        }

        private bool TryAllocate(
            int memoryTypeIndex, 
            ulong size, 
            ulong alignment, 
            bool map, 
            bool isBuffer, 
            ref MemoryAllocation allocation)
        {
            _lock.EnterReadLock();
            try
            {
                for (int i = 0; i < _blockLists.Count; i++)
                {
                    var bl = _blockLists[i];
                    if (bl.MemoryTypeIndex == memoryTypeIndex && bl.ForBuffer == isBuffer)
                    {
                        allocation = bl.Allocate(size, alignment, map);
                        if (allocation.IsValid)
                        {
                            return true;
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
                // 再次检查读锁区域（可能其他线程已创建）
                for (int i = 0; i < _blockLists.Count; i++)
                {
                    var bl = _blockLists[i];
                    if (bl.MemoryTypeIndex == memoryTypeIndex && bl.ForBuffer == isBuffer)
                    {
                        allocation = bl.Allocate(size, alignment, map);
                        if (allocation.IsValid)
                        {
                            return true;
                        }
                    }
                }

                // 创建新内存块
                var newBl = new MemoryAllocatorBlockList(_api, _device, memoryTypeIndex, _blockAlignment, isBuffer);
                _blockLists.Add(newBl);

                allocation = newBl.Allocate(size, alignment, map);
                return allocation.IsValid;
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

        public void Dispose()
        {
            for (int i = 0; i < _blockLists.Count; i++)
            {
                _blockLists[i].Dispose();
            }
        }
    }
}
