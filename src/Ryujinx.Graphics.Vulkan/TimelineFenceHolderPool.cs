using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// 简化的时间线信号量等待器
    /// </summary>
    class TimelineFenceHolder
    {
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly Semaphore _timelineSemaphore;
        private readonly Dictionary<int, List<ulong>> _commandBufferSignals;
        
        public TimelineFenceHolder(VulkanRenderer gd, Device device, Semaphore timelineSemaphore)
        {
            _gd = gd;
            _device = device;
            _timelineSemaphore = timelineSemaphore;
            _commandBufferSignals = new Dictionary<int, List<ulong>>();
        }
        
        /// <summary>
        /// 批量添加时间线信号量值
        /// </summary>
        public void AddSignals(int cbIndex, ulong[] values)
        {
            if (values == null || values.Length == 0)
                return;
                
            lock (_commandBufferSignals)
            {
                if (!_commandBufferSignals.ContainsKey(cbIndex))
                {
                    _commandBufferSignals[cbIndex] = new List<ulong>();
                }
                
                var list = _commandBufferSignals[cbIndex];
                foreach (var value in values)
                {
                    // 确保值递增且不重复
                    if (list.Count == 0 || value > list[list.Count - 1])
                    {
                        list.Add(value);
                    }
                }
            }
        }
        
        /// <summary>
        /// 添加单个时间线信号量值
        /// </summary>
        public void AddSignal(ulong value)
        {
            AddSignals(-1, new ulong[] { value }); // -1表示无特定缓冲区
        }
        
        /// <summary>
        /// 添加单个时间线信号量值到特定缓冲区
        /// </summary>
        public void AddSignal(int cbIndex, ulong value)
        {
            AddSignals(cbIndex, new ulong[] { value });
        }
        
        /// <summary>
        /// 移除命令缓冲区的所有信号
        /// </summary>
        public void RemoveSignals(int cbIndex)
        {
            lock (_commandBufferSignals)
            {
                _commandBufferSignals.Remove(cbIndex);
            }
        }
        
        /// <summary>
        /// 获取命令缓冲区的最大信号量值
        /// </summary>
        public ulong GetMaxSignalValue(int cbIndex)
        {
            lock (_commandBufferSignals)
            {
                if (_commandBufferSignals.TryGetValue(cbIndex, out var list) && list.Count > 0)
                {
                    return list[list.Count - 1]; // 列表已排序，最后一个最大
                }
                return 0;
            }
        }
        
        /// <summary>
        /// 获取所有命令缓冲区中的最大信号量值
        /// </summary>
        public ulong GetGlobalMaxSignalValue()
        {
            lock (_commandBufferSignals)
            {
                ulong maxValue = 0;
                foreach (var kvp in _commandBufferSignals)
                {
                    var list = kvp.Value;
                    if (list.Count > 0)
                    {
                        ulong listMax = list[list.Count - 1];
                        if (listMax > maxValue)
                        {
                            maxValue = listMax;
                        }
                    }
                }
                return maxValue;
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
            
            ulong targetValue = GetGlobalMaxSignalValue();
            if (targetValue == 0)
            {
                return true; // 没有需要等待的信号
            }
            
            return WaitForTimelineValue(api, device, targetValue, timeout);
        }
        
        /// <summary>
        /// 等待时间线信号量达到特定值
        /// </summary>
        public unsafe bool WaitForTimelineValue(Vk api, Device device, ulong targetValue, ulong timeout)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0)
            {
                return true;
            }
            
            // 首先检查是否已经达到目标值
            ulong currentValue = _gd.GetTimelineSemaphoreValue();
            if (currentValue >= targetValue)
            {
                return true;
            }
            
            // 使用栈分配来避免GC
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
            
            if (result == Result.Success)
            {
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量等待成功: 目标值={targetValue}");
            }
            else if (result == Result.Timeout)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量等待超时: 目标值={targetValue}, 当前值={_gd.GetTimelineSemaphoreValue()}");
            }
            else
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"时间线信号量等待失败: 目标值={targetValue}, 错误={result}");
            }
            
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