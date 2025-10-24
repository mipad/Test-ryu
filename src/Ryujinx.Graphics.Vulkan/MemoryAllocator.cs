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

        // 资源回收回调
        public event Action OnMemoryPressure;

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
            return Allocate(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
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
                
                // 订阅内存压力事件
                newBl.OnMemoryPressure += (blockList) => 
                {
                    OnMemoryPressure?.Invoke();
                };
                
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

        /// <summary>
        /// 手动触发全局内存回收
        /// </summary>
        public void ManualReclaim()
        {
            _lock.EnterReadLock();
            try
            {
                foreach (var blockList in _blockLists)
                {
                    blockList.ManualReclaim();
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 强制全局内存回收
        /// </summary>
        public void ForceReclaim()
        {
            _lock.EnterReadLock();
            try
            {
                foreach (var blockList in _blockLists)
                {
                    blockList.ForceReclaimMemory();
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 激进的内存回收
        /// </summary>
        public void AggressiveReclaim()
        {
            _lock.EnterReadLock();
            try
            {
                foreach (var blockList in _blockLists)
                {
                    blockList.AggressiveReclaimMemory();
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 获取内存使用统计
        /// </summary>
        public (ulong totalSize, ulong usedSize, int blockCount) GetMemoryStats()
        {
            _lock.EnterReadLock();
            try
            {
                ulong totalMemory = 0;
                ulong usedMemory = 0;
                int totalBlocks = 0;

                foreach (var blockList in _blockLists)
                {
                    var stats = blockList.GetMemoryStats();
                    totalMemory += stats.totalSize;
                    usedMemory += stats.usedSize;
                    totalBlocks += stats.blockCount;
                }

                return (totalMemory, usedMemory, totalBlocks);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 记录内存统计信息到日志
        /// </summary>
        public void LogMemoryStats()
        {
            _lock.EnterReadLock();
            try
            {
                ulong totalMemory = 0;
                ulong usedMemory = 0;
                int totalBlocks = 0;

                foreach (var blockList in _blockLists)
                {
                    var stats = blockList.GetMemoryStats();
                    totalMemory += stats.totalSize;
                    usedMemory += stats.usedSize;
                    totalBlocks += stats.blockCount;
                }

                float usagePercent = totalMemory > 0 ? (float)usedMemory / totalMemory * 100 : 0;
                // 这里可以记录到日志系统
                Console.WriteLine($"Memory Stats: {usedMemory}/{totalMemory} bytes ({usagePercent:F1}% used), {totalBlocks} blocks");
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// 触发内存压力，让上层释放资源
        /// </summary>
        public void TriggerMemoryPressure()
        {
            OnMemoryPressure?.Invoke();
        }

        public void Dispose()
        {
            for (int i = 0; i < _blockLists.Count; i++)
            {
                _blockLists[i].Dispose();
            }
            _blockLists.Clear();
        }
    }
}
