using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// 基于时间线信号量的等待器
    /// </summary>
    class TimelineFenceHolder
    {
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly Semaphore _timelineSemaphore;
        private readonly Dictionary<int, ulong> _commandBufferSignals; // 命令缓冲区索引 -> 信号量值
        
        public TimelineFenceHolder(VulkanRenderer gd, Device device, Semaphore timelineSemaphore)
        {
            _gd = gd;
            _device = device;
            _timelineSemaphore = timelineSemaphore;
            _commandBufferSignals = new Dictionary<int, ulong>();
        }
        
        /// <summary>
        /// 为命令缓冲区添加时间线信号量值
        /// </summary>
        public void AddSignal(int cbIndex, ulong value)
        {
            lock (_commandBufferSignals)
            {
                if (_commandBufferSignals.TryGetValue(cbIndex, out var existingValue))
                {
                    // 取最大值，因为时间线信号量值必须递增
                    if (value > existingValue)
                    {
                        _commandBufferSignals[cbIndex] = value;
                    }
                }
                else
                {
                    _commandBufferSignals[cbIndex] = value;
                }
            }
        }
        
        /// <summary>
        /// 移除命令缓冲区的信号
        /// </summary>
        public void RemoveSignal(int cbIndex)
        {
            lock (_commandBufferSignals)
            {
                _commandBufferSignals.Remove(cbIndex);
            }
        }
        
        /// <summary>
        /// 检查是否有等待的信号
        /// </summary>
        public bool HasSignal(int cbIndex)
        {
            lock (_commandBufferSignals)
            {
                return _commandBufferSignals.ContainsKey(cbIndex);
            }
        }
        
        /// <summary>
        /// 获取命令缓冲区的信号量值
        /// </summary>
        public ulong GetSignalValue(int cbIndex)
        {
            lock (_commandBufferSignals)
            {
                return _commandBufferSignals.TryGetValue(cbIndex, out var value) ? value : 0;
            }
        }
        
        /// <summary>
        /// 等待所有信号
        /// </summary>
        public bool WaitForSignals(Vk api, Device device, ulong timeout = 0)
        {
            if (_timelineSemaphore.Handle == 0 || !_gd.SupportsTimelineSemaphores)
            {
                return true; // 不支持时间线信号量，假设已发出信号
            }
            
            lock (_commandBufferSignals)
            {
                if (_commandBufferSignals.Count == 0)
                {
                    return true; // 没有需要等待的信号
                }
                
                // 获取最大的信号量值
                ulong maxValue = 0;
                foreach (var value in _commandBufferSignals.Values)
                {
                    if (value > maxValue)
                    {
                        maxValue = value;
                    }
                }
                
                if (maxValue == 0)
                {
                    return true;
                }
                
                // 获取当前信号量值
                ulong currentValue = _gd.GetTimelineSemaphoreValue();
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"TimelineFenceHolder等待: 当前值={currentValue}, 目标值={maxValue}");
                
                if (currentValue >= maxValue)
                {
                    return true; // 已经达到目标值
                }
                
                // 等待时间线信号量达到目标值
                return WaitForTimelineValue(api, device, maxValue, timeout);
            }
        }
        
        /// <summary>
        /// 等待特定范围的信号
        /// </summary>
        public bool WaitForSignals(Vk api, Device device, int offset, int size, ulong timeout = 0)
        {
            // 对于时间线信号量，我们不跟踪缓冲区使用情况
            // 如果需要，可以实现类似BufferUsageBitmap的功能
            return WaitForSignals(api, device, timeout);
        }
        
        /// <summary>
        /// 等待时间线信号量达到特定值
        /// </summary>
        private unsafe bool WaitForTimelineValue(Vk api, Device device, ulong targetValue, ulong timeout)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0)
            {
                return true;
            }
            
            // 简化方法：直接调用Vulkan API，不尝试获取结构体的地址
            // 我们可以使用stackalloc在栈上分配数组
            Semaphore* pSemaphore = stackalloc Semaphore[1];
            ulong* pValue = stackalloc ulong[1];
            
            // 将值复制到栈上分配的内存中
            *pSemaphore = _timelineSemaphore;
            *pValue = targetValue;
            
            // 现在直接使用这些指针，它们已经是固定的（在栈上）
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = pSemaphore,
                PValues = pValue
            };
            
            var result = _gd.TimelineSemaphoreApi.WaitSemaphores(device, &waitInfo, timeout);
            
            return result == Result.Success;
        }
        
        /// <summary>
        /// 清理所有信号
        /// </summary>
        public void Clear()
        {
            lock (_commandBufferSignals)
            {
                _commandBufferSignals.Clear();
            }
        }
    }
}