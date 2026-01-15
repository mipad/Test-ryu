using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// 基于时间线信号量的批量等待器
    /// </summary>
    class TimelineFenceHolder
    {
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly Semaphore _timelineSemaphore;
        private readonly Dictionary<int, List<ulong>> _commandBufferSignals; // 命令缓冲区索引 -> 信号量值列表
        private readonly List<BulkSignalBatch> _pendingBatches;
        private readonly object _batchLock = new object();
        private ulong _lastProcessedValue;
        
        // 批量信号批次
        private class BulkSignalBatch
        {
            public ulong[] Values;
            public int Count;
            public ulong MaxValue;
            public Stopwatch Timer;
            
            public BulkSignalBatch(int capacity)
            {
                Values = new ulong[capacity];
                Count = 0;
                MaxValue = 0;
                Timer = Stopwatch.StartNew();
            }
            
            public bool TryAdd(ulong value)
            {
                if (Count < Values.Length)
                {
                    Values[Count++] = value;
                    if (value > MaxValue)
                    {
                        MaxValue = value;
                    }
                    return true;
                }
                return false;
            }
            
            public bool ShouldFlush(long maxDelayMs)
            {
                return Timer.ElapsedMilliseconds >= maxDelayMs;
            }
        }
        
        public TimelineFenceHolder(VulkanRenderer gd, Device device, Semaphore timelineSemaphore)
        {
            _gd = gd;
            _device = device;
            _timelineSemaphore = timelineSemaphore;
            _commandBufferSignals = new Dictionary<int, List<ulong>>();
            _pendingBatches = new List<BulkSignalBatch>();
            _lastProcessedValue = 0;
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
            
            // 将值添加到批量处理队列
            AddToBatchQueue(values);
        }
        
        /// <summary>
        /// 添加单个时间线信号量值
        /// </summary>
        public void AddSignal(int cbIndex, ulong value)
        {
            AddSignals(cbIndex, new ulong[] { value });
        }
        
        /// <summary>
        /// 将信号量值添加到批量队列
        /// </summary>
        private void AddToBatchQueue(ulong[] values)
        {
            lock (_batchLock)
            {
                // 尝试添加到现有的批次
                bool added = false;
                foreach (var batch in _pendingBatches)
                {
                    if (batch.TryAdd(values[0])) // 先添加第一个值
                    {
                        added = true;
                        // 如果有多个值，创建新批次
                        for (int i = 1; i < values.Length; i++)
                        {
                            var newBatch = new BulkSignalBatch(16);
                            newBatch.TryAdd(values[i]);
                            _pendingBatches.Add(newBatch);
                        }
                        break;
                    }
                }
                
                // 如果没有可用的批次，创建新批次
                if (!added)
                {
                    var batch = new BulkSignalBatch(16);
                    batch.TryAdd(values[0]);
                    _pendingBatches.Add(batch);
                    
                    // 如果有多个值，创建额外的批次
                    for (int i = 1; i < values.Length; i++)
                    {
                        var extraBatch = new BulkSignalBatch(16);
                        extraBatch.TryAdd(values[i]);
                        _pendingBatches.Add(extraBatch);
                    }
                }
            }
        }
        
        /// <summary>
        /// 检查并刷新需要提交的批次
        /// </summary>
        public void FlushPendingBatches(int maxDelayMs = 10, int minBatchSize = 8)
        {
            lock (_batchLock)
            {
                if (_pendingBatches.Count == 0)
                    return;
                    
                List<BulkSignalBatch> toRemove = new List<BulkSignalBatch>();
                List<ulong> valuesToFlush = new List<ulong>();
                
                foreach (var batch in _pendingBatches)
                {
                    // 检查批次是否需要刷新
                    if (batch.Count >= minBatchSize || batch.ShouldFlush(maxDelayMs))
                    {
                        // 收集这个批次的所有值
                        for (int i = 0; i < batch.Count; i++)
                        {
                            valuesToFlush.Add(batch.Values[i]);
                        }
                        toRemove.Add(batch);
                    }
                }
                
                // 移除已处理的批次
                foreach (var batch in toRemove)
                {
                    _pendingBatches.Remove(batch);
                }
                
                // 如果有需要刷新的值，提交它们
                if (valuesToFlush.Count > 0)
                {
                    SubmitBulkSignals(valuesToFlush.ToArray());
                }
            }
        }
        
        /// <summary>
        /// 批量提交时间线信号量值
        /// </summary>
        private unsafe void SubmitBulkSignals(ulong[] values)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0)
                return;
                
            if (values == null || values.Length == 0)
                return;
                
            // 排序并去重
            Array.Sort(values);
            List<ulong> uniqueValues = new List<ulong>();
            ulong lastValue = 0;
            foreach (var value in values)
            {
                if (value > lastValue)
                {
                    uniqueValues.Add(value);
                    lastValue = value;
                }
            }
            
            if (uniqueValues.Count == 0)
                return;
                
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"批量提交时间线信号量: 数量={uniqueValues.Count}, 最大值={uniqueValues[uniqueValues.Count - 1]}");
            
            // 创建专门的命令缓冲区来批量发送信号
            var cbs = _gd.CommandBufferPool.Rent();
            
            try
            {
                // 添加所有信号量值
                foreach (var value in uniqueValues)
                {
                    _gd.CommandBufferPool.AddTimelineSignalToBuffer(cbs.CommandBufferIndex, _timelineSemaphore, value);
                }
                
                // 立即提交
                _gd.EndAndSubmitCommandBuffer(cbs, 0); // 0表示不需要额外的信号值
            }
            finally
            {
                // 注意：EndAndSubmitCommandBuffer已经处理了返回
            }
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
        /// 检查是否有等待的信号
        /// </summary>
        public bool HasSignals(int cbIndex)
        {
            lock (_commandBufferSignals)
            {
                return _commandBufferSignals.ContainsKey(cbIndex) && 
                       _commandBufferSignals[cbIndex].Count > 0;
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
        /// 获取所有待处理的信号量值
        /// </summary>
        public ulong[] GetPendingSignals()
        {
            lock (_batchLock)
            {
                List<ulong> allValues = new List<ulong>();
                foreach (var batch in _pendingBatches)
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        allValues.Add(batch.Values[i]);
                    }
                }
                return allValues.ToArray();
            }
        }
        
        /// <summary>
        /// 等待所有信号（批量等待）
        /// </summary>
        public bool WaitForSignals(Vk api, Device device, ulong timeout = 0)
        {
            if (_timelineSemaphore.Handle == 0 || !_gd.SupportsTimelineSemaphores)
            {
                return true; // 不支持时间线信号量，假设已发出信号
            }
            
            // 首先刷新所有待处理的批次
            FlushPendingBatches(0, 1); // 强制刷新所有批次
            
            lock (_commandBufferSignals)
            {
                if (_commandBufferSignals.Count == 0)
                {
                    return true; // 没有需要等待的信号
                }
                
                // 获取所有命令缓冲区中的最大信号量值
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
                
                if (maxValue == 0)
                {
                    return true;
                }
                
                // 获取当前信号量值
                ulong currentValue = _gd.GetTimelineSemaphoreValue();
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"TimelineFenceHolder批量等待: 当前值={currentValue}, 目标值={maxValue}");
                
                if (currentValue >= maxValue)
                {
                    return true; // 已经达到目标值
                }
                
                // 等待时间线信号量达到目标值
                return WaitForTimelineValue(api, device, maxValue, timeout);
            }
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
                _lastProcessedValue = targetValue;
            }
            
            return result == Result.Success;
        }
        
        /// <summary>
        /// 批量等待多个信号量值
        /// </summary>
        public unsafe bool WaitForMultipleSignals(Vk api, Device device, ulong[] targetValues, ulong timeout = 0)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0 || targetValues == null || targetValues.Length == 0)
            {
                return true;
            }
            
            // 找到最大值
            ulong maxValue = 0;
            foreach (var value in targetValues)
            {
                if (value > maxValue)
                {
                    maxValue = value;
                }
            }
            
            // 只等待最大值，因为时间线信号量是有序的
            return WaitForTimelineValue(api, device, maxValue, timeout);
        }
        
        /// <summary>
        /// 检查信号是否已经发出（非阻塞）
        /// </summary>
        public bool AreSignalsComplete(ulong[] targetValues)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0 || targetValues == null)
            {
                return true;
            }
            
            ulong currentValue = _gd.GetTimelineSemaphoreValue();
            
            foreach (var target in targetValues)
            {
                if (currentValue < target)
                {
                    return false;
                }
            }
            
            return true;
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
            
            lock (_batchLock)
            {
                _pendingBatches.Clear();
            }
            
            _lastProcessedValue = 0;
        }
        
        /// <summary>
        /// 获取已处理的最后一个信号量值
        /// </summary>
        public ulong GetLastProcessedValue()
        {
            return _lastProcessedValue;
        }
        
        /// <summary>
        /// 获取当前时间线信号量的值
        /// </summary>
        public ulong GetCurrentTimelineValue()
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0)
            {
                return 0;
            }
            
            return _gd.GetTimelineSemaphoreValue();
        }
    }
}