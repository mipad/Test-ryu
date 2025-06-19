using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    class MemoryAllocator : IDisposable
    {
        // Android平台使用更保守的内存设置
        #if ANDROID
        private const ulong MaxDeviceMemoryUsageEstimate = 4UL * 1024 * 1024 * 1024; // 4GB
        private const ulong LargeAllocationThreshold = 64 * 1024 * 1024; // 64MB
        private const int MaxRetryAttempts = 5; // 增加重试次数
        #else
        private const ulong MaxDeviceMemoryUsageEstimate = 16UL * 1024 * 1024 * 1024; // 16GB
        private const ulong LargeAllocationThreshold = 256 * 1024 * 1024; // 256MB
        private const int MaxRetryAttempts = 3;
        #endif

        private readonly Vk _api;
        private readonly VulkanPhysicalDevice _physicalDevice;
        private readonly Device _device;
        private readonly List<MemoryAllocatorBlockList> _blockLists;
        private readonly int _blockAlignment;
        private readonly ReaderWriterLockSlim _lock;
        
        // 添加内存压力回调
        public event Action<ulong, ulong> OnMemoryPressure;
        
        // 大内存分配节流机制
        private readonly Dictionary<int, DateTime> _lastLargeAllocTime = new();
        private readonly object _allocationThrottleLock = new();

        public MemoryAllocator(Vk api, VulkanPhysicalDevice physicalDevice, Device device)
        {
            _api = api;
            _physicalDevice = physicalDevice;
            _device = device;
            _blockLists = new List<MemoryAllocatorBlockList>();
            _blockAlignment = (int)Math.Min(int.MaxValue, MaxDeviceMemoryUsageEstimate / _physicalDevice.PhysicalDeviceProperties.Limits.MaxMemoryAllocationCount);
            _lock = new(LockRecursionPolicy.NoRecursion);
            
            Logger.Info?.Print(LogClass.Gpu, 
                $"MemoryAllocator initialized: MaxBlockSize={FormatSize((ulong)_blockAlignment)}, " +
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
                    $"No suitable memory type found for flags: {flags}");
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

            // Android平台：大内存分配节流
            #if ANDROID
            if (requirements.Size > LargeAllocationThreshold)
            {
                ThrottleLargeAllocations(memoryTypeIndex, requirements.Size);
            }
            #endif

            return AllocateWithRetry(memoryTypeIndex, requirements.Size, requirements.Alignment, map, isBuffer);
        }

        // Android专用：大内存分配节流机制
        private void ThrottleLargeAllocations(int memoryTypeIndex, ulong size)
        {
            const int MinDelayMs = 50;
            const int MaxDelayMs = 500;
            const double DelayMultiplier = 1.5;

            lock (_allocationThrottleLock)
            {
                if (_lastLargeAllocTime.TryGetValue(memoryTypeIndex, out DateTime lastTime))
                {
                    var elapsed = DateTime.UtcNow - lastTime;
                    int baseDelay = (int)(MinDelayMs * Math.Pow(DelayMultiplier, _lastLargeAllocTime.Count));
                    int requiredDelay = Math.Min(MaxDelayMs, baseDelay);
                    
                    if (elapsed.TotalMilliseconds < requiredDelay)
                    {
                        int delay = (int)(requiredDelay - elapsed.TotalMilliseconds);
                        Logger.Warning?.Print(LogClass.Gpu, 
                            $"Throttling large allocation ({FormatSize(size)}) for {delay}ms");
                        Thread.Sleep(delay);
                    }
                }
                _lastLargeAllocTime[memoryTypeIndex] = DateTime.UtcNow;
            }
        }

        private MemoryAllocation AllocateWithRetry(int memoryTypeIndex, ulong size, ulong alignment, bool map, bool isBuffer)
        {
            int attempt = 0;

            while (attempt++ < MaxRetryAttempts)
            {
                var allocation = Allocate(memoryTypeIndex, size, alignment, map, isBuffer);
                
                // 使用Handle检查分配是否有效
                if (allocation.Memory.Handle != 0)
                {
                    return allocation;
                }

                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Memory allocation failed for {FormatSize(size)} (attempt {attempt}/{MaxRetryAttempts})");
                
                // 触发内存清理回调
                OnMemoryPressure?.Invoke(size, (ulong)attempt);
                
                // 指数退避等待
                int waitTime = 50 * (int)Math.Pow(2, attempt - 1);
                Thread.Sleep(waitTime);
            }

            Logger.Error?.Print(LogClass.Gpu, 
                $"Memory allocation failed after {MaxRetryAttempts} attempts: {FormatSize(size)}");
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
            
            Logger.Info?.Print(LogClass.Gpu, "MemoryAllocator disposed");
        }
    }
}
