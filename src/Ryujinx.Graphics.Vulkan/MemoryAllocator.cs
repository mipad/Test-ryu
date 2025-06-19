using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
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
            // 创建优先级降序的标志组合列表
            var flagCombinations = new List<MemoryPropertyFlags>
            {
                // 1. 首选：完整请求的标志
                flags,
                
                // 2. 次选：去掉 HostCachedBit（移动设备常见限制）
                flags & ~MemoryPropertyFlags.HostCachedBit,
                
                // 3. 保底：仅保留必要标志
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            };

            // 添加 Android 专用回退方案
            #if ANDROID
            flagCombinations.AddRange(new[]
            {
                // 4. Android 专用：某些设备需要 DeviceLocalBit
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.DeviceLocalBit,
                
                // 5. Android 专用：某些设备不支持 HostCoherentBit
                MemoryPropertyFlags.HostVisibleBit,
                
                // 6. Android 最终回退：仅设备本地内存
                MemoryPropertyFlags.DeviceLocalBit
            });
            #endif

            // 尝试所有标志组合
            foreach (var flagCombo in flagCombinations.Distinct())
            {
                int memoryTypeIndex = FindSuitableMemoryTypeIndex(
                    requirements.MemoryTypeBits, 
                    flagCombo
                );

                if (memoryTypeIndex >= 0)
                {
                    // 记录回退决策（如果与原始请求不同）
                    if (flagCombo != flags)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Using fallback memory flags: {flagCombo} " +
                            $"instead of requested: {flags}");
                    }

                    bool map = flagCombo.HasFlag(MemoryPropertyFlags.HostVisibleBit);
                    
                    // 大内存分配警告
                    if (requirements.Size > LargeAllocationThreshold)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Allocating large buffer: {FormatSize(requirements.Size)} " +
                            $"(Type: {memoryTypeIndex}, Flags: {flagCombo})");
                    }

                    return AllocateWithRetry(
                        memoryTypeIndex, 
                        requirements.Size, 
                        requirements.Alignment, 
                        map, 
                        isBuffer
                    );
                }
            }

            // 所有尝试均失败
            Logger.Error?.Print(LogClass.Gpu, 
                $"All memory allocation strategies failed for flags: {flags}");
            return default;
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
}
