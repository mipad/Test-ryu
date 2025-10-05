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

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
            
            // 设置内存限制（Android设备通常有更严格的内存限制）
            _memoryLimit = CalculateMemoryLimit(physicalDevice);
            _currentUsage = 0;
            
            Logger.Info?.Print(LogClass.Gpu, $"Memory allocator initialized with limit: 0x{_memoryLimit:X}");
        }

        private ulong CalculateMemoryLimit(VulkanPhysicalDevice physicalDevice)
        {
            // 在Android上，使用更保守的内存限制
            ulong totalDeviceMemory = 0;
            
            for (int i = 0; i < physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeapCount; i++)
            {
                var heap = physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                {
                    totalDeviceMemory += heap.Size;
                }
            }
            
            // 对于Android设备，限制为总设备内存的50%或512MB，取较小值
            ulong androidLimit = Math.Min(totalDeviceMemory / 2, 512 * 1024 * 1024);
            return androidLimit;
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
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
            _currentUsage -= size;
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
        public (ulong totalAllocated, ulong totalFreed, ulong currentUsage, ulong memoryLimit) GetMemoryStatistics()
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

            return (totalAllocated, totalFreed, currentUsage, _memoryLimit);
        }

        // 添加内存压缩方法（尝试合并空闲块）
        public void CompactMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                // 这里可以实现内存压缩逻辑
                // 目前只是记录调用
                Logger.Info?.Print(LogClass.Gpu, "Memory compaction requested");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
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