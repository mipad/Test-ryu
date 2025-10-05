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

        // 智能分配策略
        private int _largeAllocationAttempts;
        private int _deviceLostRecoveryCount;
        private ulong _maxSuccessfulAllocation;
        private ulong _lastFailedAllocationSize;
        private DateTime _lastDeviceLostTime;
        private readonly List<ulong> _successfulAllocationSizes;

        private const int MaxLargeAllocationAttempts = 2;
        private const int MaxDeviceLostRecoveryAttempts = 3;

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
            _maxSuccessfulAllocation = 64 * 1024 * 1024; // 初始假设64MB是安全的
            _lastFailedAllocationSize = 0;
            _successfulAllocationSizes = new List<ulong>();
            _pressureState = MemoryPressureState.Normal;
            _lastMemoryCleanup = DateTime.Now;
            _lastDeviceLostTime = DateTime.MinValue;
            
            Logger.Info?.Print(LogClass.Gpu, $"Memory allocator initialized. Initial safe allocation size: {_maxSuccessfulAllocation / (1024 * 1024)}MB");
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

            // 使用智能大小调整
            ulong adjustedSize = GetAdjustedAllocationSize(requirements.Size);
            if (adjustedSize != requirements.Size)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Adjusting allocation size from 0x{requirements.Size:X} to 0x{adjustedSize:X} based on historical data");
                requirements.Size = adjustedSize;
            }

            // 对于大内存分配，使用特殊处理
            if (requirements.Size > 32 * 1024 * 1024) // 32MB阈值
            {
                return AllocateLargeMemory(requirements, flags, isBuffer);
            }

            // 常规内存分配
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private ulong GetAdjustedAllocationSize(ulong requestedSize)
        {
            // 如果请求大小小于已知安全大小，直接返回
            if (requestedSize <= _maxSuccessfulAllocation)
            {
                return requestedSize;
            }

            // 如果最近有失败记录，使用二分查找策略
            if (_lastFailedAllocationSize > 0 && requestedSize > _lastFailedAllocationSize)
            {
                // 在最后成功和最后失败之间寻找安全大小
                ulong safeSize = FindSafeAllocationSize(requestedSize);
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Using safe allocation size: 0x{safeSize:X} instead of 0x{requestedSize:X}");
                return safeSize;
            }

            return requestedSize;
        }

        private ulong FindSafeAllocationSize(ulong requestedSize)
        {
            // 二分查找策略：在已知安全大小和请求大小之间寻找
            ulong low = _maxSuccessfulAllocation;
            ulong high = Math.Min(requestedSize, _lastFailedAllocationSize > 0 ? _lastFailedAllocationSize : requestedSize);
            
            // 如果高低差距不大，直接使用安全大小
            if (high - low < 16 * 1024 * 1024) // 16MB以内
            {
                return low;
            }

            // 使用二分法找到中间值
            ulong mid = low + (high - low) / 2;
            
            // 优先选择2的幂次方大小，这对GPU更友好
            mid = RoundToNearestPowerOfTwo(mid);
            
            return Math.Max(low, Math.Min(mid, 256 * 1024 * 1024)); // 最大256MB
        }

        private ulong RoundToNearestPowerOfTwo(ulong size)
        {
            ulong power = 1;
            while (power < size)
            {
                power <<= 1;
            }
            
            // 选择较小的那个，更安全
            ulong lowerPower = power >> 1;
            if (size - lowerPower < power - size)
            {
                return lowerPower;
            }
            return power;
        }

        private bool ShouldThrottleDueToDeviceLost()
        {
            if (_deviceLostRecoveryCount > 0)
            {
                TimeSpan timeSinceLastLost = DateTime.Now - _lastDeviceLostTime;
                if (timeSinceLastLost.TotalSeconds < 5 * _deviceLostRecoveryCount)
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
                $"Device lost detected! Recovery attempt #{_deviceLostRecoveryCount}.");
            
            // 重置大分配计数
            _largeAllocationAttempts = 0;
            
            // 强制清理内存
            CompactMemory();
            ForceGarbageCollection();
        }

        public void NotifyAllocationSuccess(ulong size)
        {
            // 记录成功的分配大小
            _successfulAllocationSizes.Add(size);
            if (size > _maxSuccessfulAllocation)
            {
                _maxSuccessfulAllocation = size;
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Updated max successful allocation size: {_maxSuccessfulAllocation / (1024 * 1024)}MB");
            }

            // 保持列表大小合理
            if (_successfulAllocationSizes.Count > 100)
            {
                _successfulAllocationSizes.RemoveAt(0);
            }
        }

        public void NotifyAllocationFailure(ulong size)
        {
            _lastFailedAllocationSize = size;
            Logger.Warning?.Print(LogClass.Gpu, 
                $"Allocation failure recorded for size: 0x{size:X}. Updating safety limits.");
            
            // 如果失败大小小于当前最大成功大小，调整最大成功大小
            if (size < _maxSuccessfulAllocation)
            {
                _maxSuccessfulAllocation = Math.Min(_maxSuccessfulAllocation, size / 2);
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Reduced max successful allocation to: {_maxSuccessfulAllocation / (1024 * 1024)}MB");
            }
        }

        private void UpdateMemoryPressureState()
        {
            var systemMemoryInfo = GetSystemMemoryInfo();
            double usageRatio = systemMemoryInfo.totalMemory > 0 ? 
                (double)_currentUsage / systemMemoryInfo.totalMemory : 0;

            // 宽松的压力阈值
            MemoryPressureState newState = usageRatio switch
            {
                < 0.5 => MemoryPressureState.Normal,    // 50%以下：正常
                < 0.7 => MemoryPressureState.Moderate,  // 50-70%：中等
                < 0.85 => MemoryPressureState.High,     // 70-85%：高
                _ => MemoryPressureState.Critical       // 85%以上：临界
            };

            if (newState != _pressureState)
            {
                Logger.Info?.Print(LogClass.Gpu, 
                    $"Memory pressure: {_pressureState} -> {newState} (Usage: {usageRatio:P})");
                _pressureState = newState;
            }

            // 在高压状态下定期清理内存
            if (_pressureState >= MemoryPressureState.Moderate && 
                (DateTime.Now - _lastMemoryCleanup).TotalSeconds > 60)
            {
                CompactMemory();
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
                    totalDeviceMemory += heap.Size;
                }

                // 对于统一内存架构，系统内存 ≈ 设备内存
                ulong estimatedSystemMemory = totalDeviceMemory;
                ulong availableMemory = estimatedSystemMemory > _currentUsage ? 
                    estimatedSystemMemory - _currentUsage : 0;

                return (estimatedSystemMemory, availableMemory);
            }
            catch
            {
                return (4UL * 1024 * 1024 * 1024, 3UL * 1024 * 1024 * 1024);
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

            Logger.Info?.Print(LogClass.Gpu, 
                $"Large allocation attempt #{_largeAllocationAttempts}: " +
                $"Size=0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB), " +
                $"SafeMax=0x{_maxSuccessfulAllocation:X} ({_maxSuccessfulAllocation / (1024 * 1024)}MB), " +
                $"Available=0x{availableMemory:X} ({availableMemory / (1024 * 1024)}MB)");

            // 检查设备丢失恢复状态
            if (_deviceLostRecoveryCount >= MaxDeviceLostRecoveryAttempts)
            {
                Logger.Error?.Print(LogClass.Gpu, "Too many device loss recoveries, rejecting allocation");
                return default;
            }

            // 检查是否超过已知安全大小
            if (requirements.Size > _maxSuccessfulAllocation * 2)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Requested size significantly exceeds known safe size. Being cautious.");
            }

            // 正常分配流程
            return AllocateRegularMemory(requirements, flags, isBuffer);
        }

        private MemoryAllocation AllocateRegularMemory(
            MemoryRequirements requirements,
            MemoryPropertyFlags flags,
            bool isBuffer)
        {
            var systemMemoryInfo = GetSystemMemoryInfo();
            double usageRatio = systemMemoryInfo.totalMemory > 0 ? 
                (double)_currentUsage / systemMemoryInfo.totalMemory : 0;
                
            // 只有在极端情况下才拒绝分配
            if (_pressureState >= MemoryPressureState.Critical && usageRatio > 0.9)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Critical memory pressure ({usageRatio:P}), rejecting allocation");
                return default;
            }

            int memoryTypeIndex = FindSuitableMemoryTypeIndex(requirements.MemoryTypeBits, flags);
            if (memoryTypeIndex < 0)
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"No suitable memory type found: TypeBits=0x{requirements.MemoryTypeBits:X}, Flags={flags}");
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
                    
                    // 记录成功分配
                    NotifyAllocationSuccess(requirements.Size);
                    
                    if (requirements.Size > 32 * 1024 * 1024)
                    {
                        Logger.Info?.Print(LogClass.Gpu, 
                            $"Large allocation successful: 0x{requirements.Size:X} ({requirements.Size / (1024 * 1024)}MB)");
                    }
                }
                return allocation;
            }
            catch (VulkanException ex)
            {
                Logger.Error?.Print(LogClass.Gpu, 
                    $"Allocation failed: Size=0x{requirements.Size:X}, Error={ex.Message}");
                
                // 记录失败
                NotifyAllocationFailure(requirements.Size);
                
                // 检查是否是设备丢失错误
                if (ex.Message.Contains("ErrorDeviceLost") || ex.Result == Result.ErrorDeviceLost)
                {
                    NotifyDeviceLost();
                }
                
                return default;
            }
        }

        // 其余方法保持不变...
        internal void NotifyMemoryFreed(ulong size)
        {
            if (size > _currentUsage)
            {
                _currentUsage = 0;
            }
            else
            {
                _currentUsage -= size;
            }
            _totalFrees++;
            
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

        internal int FindSuitableMemoryTypeIndex(uint memoryTypeBits, MemoryPropertyFlags flags)
        {
            // 首先尝试完全匹配
            for (int i = 0; i < _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypeCount; i++)
            {
                var type = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypes[i];
                if ((memoryTypeBits & (1 << i)) != 0 && type.PropertyFlags.HasFlag(flags))
                {
                    return i;
                }
            }

            // 尝试包含所需标志的内存类型
            for (int i = 0; i < _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypeCount; i++)
            {
                var type = _physicalDevice.PhysicalDeviceMemoryProperties.MemoryTypes[i];
                if ((memoryTypeBits & (1 << i)) != 0 && (type.PropertyFlags & flags) == flags)
                {
                    return i;
                }
            }

            // 最后尝试任何可用的内存类型
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

        public (ulong totalAllocations, ulong totalFrees, ulong currentUsage, ulong peakUsage, 
                MemoryPressureState pressureState, ulong maxSuccessfulAllocation) GetMemoryStatistics()
        {
            return (_totalAllocations, _totalFrees, _currentUsage, _peakUsage, _pressureState, _maxSuccessfulAllocation);
        }

        public void CompactMemory()
        {
            _lock.EnterWriteLock();
            try
            {
                Logger.Info?.Print(LogClass.Gpu, "Memory compaction");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void ForceGarbageCollection()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public void ResetLargeAllocationCount()
        {
            _largeAllocationAttempts = 0;
        }

        public void ResetDeviceLostRecovery()
        {
            if (_deviceLostRecoveryCount > 0)
            {
                Logger.Info?.Print(LogClass.Gpu, $"Reset device lost recovery count: {_deviceLostRecoveryCount} -> 0");
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
