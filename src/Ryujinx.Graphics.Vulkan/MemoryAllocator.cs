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

        // 内存使用统计
        private ulong _currentUsage;
        private ulong _peakUsage;
        private ulong _totalAllocations;
        private ulong _totalFrees;

        // 添加大分配计数和设备丢失保护
        private int _largeAllocationAttempts;
        private int _deviceLostRecoveryCount;
        private const int MaxLargeAllocationAttempts = 2;
        private const int MaxDeviceLostRecoveryAttempts = 3;
        private DateTime _lastDeviceLostTime;

        // 内存压力状态
        private MemoryPressureState _pressureState;
        private DateTime _lastMemoryCleanup;

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
            
            _currentUsage = 0;
            _peakUsage = 0;
            _totalAllocations = 0;
            _totalFrees = 0;
            _largeAllocationAttempts = 0;
            _deviceLostRecoveryCount = 0;
            _pressureState = MemoryPressureState.Normal;
            _lastMemoryCleanup = DateTime.Now;
            _lastDeviceLostTime = DateTime.MinValue;
            
            Logger.Info?.Print(LogClass.Gpu, "Memory allocator initialized with device-lost protection");
        }

        public MemoryAllocation AllocateDeviceMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags = 0,
            bool isBuffer = false)
        {
            // 检查设备丢失恢复状态
            if (ShouldThrottleDueToDeviceLost())
            {
                Logger.Warning?.Print(LogClass.Gpu, "Throttling memory allocation due to recent device loss");
                return default;
            }

            // 更新内存压力状态
            UpdateMemoryPressureState();

            // 对于大内存分配，使用特殊处理
            if (requirements.Size > 32 * 1024 * 1024) // 降低阈值到32MB
            {
                return AllocateLargeMemory(requirements, flags, isBuffer);
            }

            // 常规内存分配
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private bool ShouldThrottleDueToDeviceLost()
        {
            // 如果最近发生过设备丢失，限制分配频率
            if (_deviceLostRecoveryCount > 0)
            {
                TimeSpan timeSinceLastLost = DateTime.Now - _lastDeviceLostTime;
                if (timeSinceLastLost.TotalSeconds < 10 * _deviceLostRecoveryCount) // 每次设备丢失后等待时间递增
                {
                    return true;
                }
            }
            return false;
        }

        public void NotifyDeviceLost()
        {
            _deviceLostRecoveryCount++;
            _lastDeviceLostTime = DateTime.Now;
            
            Logger.Error?.Print(LogClass.Gpu, 
                $"Device lost detected! Recovery attempt #{_deviceLostRecoveryCount}. " +
                $"Throttling memory allocations for {10 * _deviceLostRecoveryCount} seconds.");
            
            // 重置大分配计数
            _largeAllocationAttempts = 0;
            
            // 强制清理内存
            CompactMemory();
            ForceGarbageCollection();
        }

        private void UpdateMemoryPressureState()
        {
            var systemMemoryInfo = GetSystemMemoryInfo();
            double usageRatio = systemMemoryInfo.totalMemory > 0 ? 
                (double)_currentUsage / systemMemoryInfo.totalMemory : 0;

            // 更保守的压力阈值，考虑设备丢失风险
            MemoryPressureState newState = usageRatio switch
            {
                < 0.4 => MemoryPressureState.Normal,    // 40%以下：正常
                < 0.6 => MemoryPressureState.Moderate,  // 40-60%：中等
                < 0.75 => MemoryPressureState.High,     // 60-75%：高
                _ => MemoryPressureState.Critical       // 75%以上：临界
            };

            if (newState != _pressureState)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Memory pressure state changed: {_pressureState} -> {newState} " +
                    $"(Usage: {_currentUsage / (1024 * 1024)}MB, Ratio: {usageRatio:P})");
                _pressureState = newState;
            }

            // 在高压状态下定期清理内存
            if (_pressureState >= MemoryPressureState.Moderate && 
                (DateTime.Now - _lastMemoryCleanup).TotalSeconds > 60)
            {
                Logger.Info?.Print(LogClass.Gpu, "Performing periodic memory cleanup");
                CompactMemory();
                ForceGarbageCollection();
                _lastMemoryCleanup = DateTime.Now;
            }
        }

        private (ulong totalMemory, ulong availableMemory) GetSystemMemoryInfo()
        {
            try
            {
                ulong totalDeviceMemory = 0;
                
                for (int i = 0; i < _physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeapCount; i++)
                {
                    var heap = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryHeaps[i];
                    if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                    {
                        totalDeviceMemory += heap.Size;
                    }
                }

                // 更保守的估算：假设系统总内存等于设备内存（对于统一内存架构）
                ulong estimatedSystemMemory = totalDeviceMemory;
                
                // 估算可用内存：总内存 - 当前使用量
                ulong availableMemory = estimatedSystemMemory > _currentUsage ? 
                    estimatedSystemMemory - _currentUsage : 0;

                return (estimatedSystemMemory, availableMemory);
            }
            catch
            {
                // 保守的回退值
                return (2UL * 1024 * 1024 * 1024, 1UL * 1024 * 1024 * 1024); // 2GB总内存，1GB可用
            }
        }

        private MemoryAllocation AllocateLargeMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            _largeAllocationAttempts++;
            
            var systemMemoryInfo = GetSystemMemoryInfo();
            ulong availableMemory = systemMemoryInfo.availableMemory;
            double usageRatio = systemMemoryInfo.totalMemory > 0 ? 
                (double)_currentUsage / systemMemoryInfo.totalMemory : 0;

            Logger.Warning?.Print(LogClass.Gpu, 
                $"Large memory allocation attempt #{_largeAllocationAttempts}: " +
                $"Size=0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB), " +
                $"CurrentUsage=0x{_currentUsage:X} ({_currentUsage / (1024 * 1024)}MB, {usageRatio:P}), " +
                $"Available=0x{availableMemory:X} ({availableMemory / (1024 * 1024)}MB)");

            // 检查设备丢失恢复状态
            if (_deviceLostRecoveryCount >= MaxDeviceLostRecoveryAttempts)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    "Rejecting large allocation due to excessive device loss recovery attempts");
                return default;
            }

            // 检查内存压力并采取相应措施
            if (_pressureState >= MemoryPressureState.High)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    "High memory pressure, being conservative with large allocation");
                return HandleConservativeAllocation(requirements, flags, isBuffer, availableMemory);
            }

            // 如果请求大小超过可用内存，尝试智能处理
            if (requirements.Size > availableMemory)
            {
                return HandleInsufficientMemory(requirements, flags, isBuffer, availableMemory);
            }

            // 正常分配流程
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private MemoryAllocation HandleConservativeAllocation(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer,
            ulong availableMemory)
        {
            // 在高压状态下，对大于128MB的分配使用分段策略
            if (requirements.Size > 128 * 1024 * 1024)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Using segmented approach for large allocation in high pressure: 0x{requirements.Size:X}");
                
                // 建议使用64MB的段大小
                ulong segmentSize = Math.Min(64UL * 1024 * 1024, availableMemory);
                if (segmentSize > 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, 
                        $"Suggesting segment size: 0x{segmentSize:X} for large allocation");
                    // 这里可以返回一个特殊错误码，让上层使用分段缓冲区
                }
                return default;
            }

            // 对于中等大小的分配，允许尝试但记录警告
            Logger.Warning?.Print(LogClass.Gpu, 
                "Proceeding with allocation in high pressure state, monitoring for device loss");
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private MemoryAllocation HandleInsufficientMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer,
            ulong availableMemory)
        {
            // 策略1: 尝试内存清理
            if (_largeAllocationAttempts <= 1)
            {
                Logger.Info?.Print(LogClass.Gpu, "Attempting memory cleanup for large allocation");
                CompactMemory();
                ForceGarbageCollection();
                
                // 重新计算可用内存
                var systemMemoryInfo = GetSystemMemoryInfo();
                availableMemory = systemMemoryInfo.availableMemory;
                
                if (requirements.Size <= availableMemory)
                {
                    Logger.Info?.Print(LogClass.Gpu, "Memory cleanup successful, proceeding with allocation");
                    return AllocateRegularMemory(requirements, flags, isBuffer);
                }
            }

            // 策略2: 对于特别大的分配，强制使用分段
            if (requirements.Size > 256 * 1024 * 1024)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Forcing segmented buffer for very large allocation: 0x{requirements.Size:X}");
                return default;
            }

            // 策略3: 在设备丢失恢复期间拒绝分配
            if (_deviceLostRecoveryCount > 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    "Rejecting allocation during device loss recovery period");
                return default;
            }

            // 策略4: 返回失败
            Logger.Error?.Print(LogClass.Gpu, 
                $"Insufficient memory for allocation: Requested=0x{requirements.Size:X}, Available=0x{availableMemory:X}");
            return default;
        }

        private MemoryAllocation AllocateRegularMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            var systemMemoryInfo = GetSystemMemoryInfo();
            double usageRatio = systemMemoryInfo.totalMemory > 0 ? 
                (double)_currentUsage / systemMemoryInfo.totalMemory : 0;
                
            // 更保守的检查，在高压状态下拒绝分配
            if (_pressureState >= MemoryPressureState.Critical)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Critical memory pressure ({usageRatio:P}), rejecting allocation: " +
                    $"Current=0x{_currentUsage:X}, Requested=0x{requirements.Size:X}");
                return default;
            }

            // 在高压状态下对中等大小分配也进行限制
            if (_pressureState >= MemoryPressureState.High && requirements.Size > 16 * 1024 * 1024)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"High memory pressure, rejecting medium allocation: 0x{requirements.Size:X}");
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
                    _peakUsage = Math.Max(_peakUsage, _currentUsage);
                    _totalAllocations++;
                    
                    // 记录大分配
                    if (requirements.Size > 32 * 1024 * 1024)
                    {
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Large memory allocation successful: 0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB), " +
                            $"New usage: 0x{_currentUsage:X} ({_currentUsage / (1024 * 1024)}MB, {usageRatio:P})");
                    }
                }
                return allocation;
            }
            catch (VulkanException ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Memory allocation failed: Size=0x{requirements.Size:X}, TypeIndex={memoryTypeIndex}, Error={ex.Message}");
                
                // 检查是否是设备丢失错误
                if (ex.Message.Contains("ErrorDeviceLost") || ex.Result == Result.ErrorDeviceLost)
                {
                    NotifyDeviceLost();
                }
                
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
            _totalFrees++;
            
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
            var systemMemoryInfo = GetSystemMemoryInfo();
            return systemMemoryInfo.availableMemory;
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
        public (ulong totalAllocations, ulong totalFrees, ulong currentUsage, ulong peakUsage, MemoryPressureState pressureState, int deviceLostRecoveryCount) GetMemoryStatistics()
        {
            return (_totalAllocations, _totalFrees, _currentUsage, _peakUsage, _pressureState, _deviceLostRecoveryCount);
        }

        // 添加内存压缩方法
        public void CompactMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                Logger.Info?.Print(LogClass.Gpu, "Memory compaction requested");
                // 这里可以实现内存压缩逻辑
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

        // 重置设备丢失计数
        public void ResetDeviceLostRecovery()
        {
            if (_deviceLostRecoveryCount > 0)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Resetting device lost recovery count: {_deviceLostRecoveryCount} -> 0");
                _deviceLostRecoveryCount = 0;
                _lastDeviceLostTime = DateTime.MinValue;
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
