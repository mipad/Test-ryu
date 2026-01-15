using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// 时间线信号量等待器池（单例）
    /// </summary>
    class TimelineFenceHolderPool : IDisposable
    {
        private static TimelineFenceHolderPool _instance;
        private static readonly object _instanceLock = new object();
        
        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly Silk.NET.Vulkan.Semaphore _timelineSemaphore; // 使用完全限定名
        private readonly ConcurrentDictionary<int, TimelineFenceHolder> _holderMap;
        private readonly object _syncLock = new object();
        private bool _disposed;
        
        // 主时间线等待器（用于大多数同步操作）
        private TimelineFenceHolder _mainHolder;
        
        // 待处理的时间线值队列
        private readonly List<ulong> _pendingValues = new();
        private readonly object _pendingLock = new();
        private Timer _flushTimer;
        private const int FlushIntervalMs = 5; // 5ms刷新一次
        
        public static TimelineFenceHolderPool GetInstance(VulkanRenderer gd, Device device, Silk.NET.Vulkan.Semaphore timelineSemaphore)
        {
            lock (_instanceLock)
            {
                if (_instance == null)
                {
                    _instance = new TimelineFenceHolderPool(gd, device, timelineSemaphore);
                }
                return _instance;
            }
        }
        
        public static bool IsInitialized => _instance != null;
        
        private TimelineFenceHolderPool(VulkanRenderer gd, Device device, Silk.NET.Vulkan.Semaphore timelineSemaphore)
        {
            _gd = gd;
            _device = device;
            _timelineSemaphore = timelineSemaphore;
            _holderMap = new ConcurrentDictionary<int, TimelineFenceHolder>();
            _mainHolder = new TimelineFenceHolder(gd, device, timelineSemaphore);
            
            // 启动定时刷新器
            _flushTimer = new Timer(FlushPendingValues, null, FlushIntervalMs, FlushIntervalMs);
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"TimelineFenceHolderPool初始化完成");
        }
        
        /// <summary>
        /// 获取主时间线等待器
        /// </summary>
        public TimelineFenceHolder GetMainHolder()
        {
            return _mainHolder;
        }
        
        /// <summary>
        /// 为特定命令缓冲区获取时间线等待器
        /// </summary>
        public TimelineFenceHolder GetHolderForBuffer(int cbIndex)
        {
            return _holderMap.GetOrAdd(cbIndex, _ => new TimelineFenceHolder(_gd, _device, _timelineSemaphore));
        }
        
        /// <summary>
        /// 添加时间线信号量值到主等待器
        /// </summary>
        public void AddSignal(ulong value)
        {
            lock (_pendingLock)
            {
                _pendingValues.Add(value);
            }
        }
        
        /// <summary>
        /// 批量添加时间线信号量值到主等待器
        /// </summary>
        public void AddSignals(ulong[] values)
        {
            if (values == null || values.Length == 0)
                return;
                
            lock (_pendingLock)
            {
                _pendingValues.AddRange(values);
            }
        }
        
        /// <summary>
        /// 为特定命令缓冲区添加时间线信号量值
        /// </summary>
        public void AddSignalToBuffer(int cbIndex, ulong value)
        {
            var holder = GetHolderForBuffer(cbIndex);
            holder.AddSignal(value);
        }
        
        /// <summary>
        /// 为特定命令缓冲区批量添加时间线信号量值
        /// </summary>
        public void AddSignalsToBuffer(int cbIndex, ulong[] values)
        {
            if (values == null || values.Length == 0)
                return;
                
            var holder = GetHolderForBuffer(cbIndex);
            holder.AddSignals(cbIndex, values);
        }
        
        /// <summary>
        /// 刷新待处理的时间线值
        /// </summary>
        private void FlushPendingValues(object state)
        {
            if (_disposed)
                return;
                
            lock (_pendingLock)
            {
                if (_pendingValues.Count == 0)
                    return;
                    
                // 批量提交到主等待器
                ulong[] values = _pendingValues.ToArray();
                _pendingValues.Clear();
                
                if (values.Length > 0)
                {
                    _mainHolder.AddSignals(-1, values); // -1表示主等待器
                    
                    // 如果需要，可以在这里批量提交到命令缓冲区
                    if (_gd.SupportsTimelineSemaphores && _timelineSemaphore.Handle != 0)
                    {
                        // 创建专门的命令缓冲区来批量发送信号
                        var cbs = _gd.CommandBufferPool.Rent();
                        try
                        {
                            foreach (var value in values)
                            {
                                _gd.CommandBufferPool.AddTimelineSignalToBuffer(cbs.CommandBufferIndex, _timelineSemaphore, value);
                            }
                            _gd.EndAndSubmitCommandBuffer(cbs, 0);
                        }
                        finally
                        {
                            // EndAndSubmitCommandBuffer已经处理返回
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 立即刷新所有待处理值
        /// </summary>
        public void FlushNow()
        {
            FlushPendingValues(null);
        }
        
        /// <summary>
        /// 等待时间线信号量达到特定值
        /// </summary>
        public bool WaitForValue(ulong targetValue, ulong timeout = 1000000000)
        {
            return _mainHolder.WaitForTimelineValue(_gd.Api, _device, targetValue, timeout);
        }
        
        /// <summary>
        /// 等待多个时间线信号量值
        /// </summary>
        public bool WaitForValues(ulong[] targetValues, ulong timeout = 1000000000)
        {
            if (targetValues == null || targetValues.Length == 0)
                return true;
                
            // 找到最大值
            ulong maxValue = 0;
            foreach (var value in targetValues)
            {
                if (value > maxValue)
                {
                    maxValue = value;
                }
            }
            
            return WaitForValue(maxValue, timeout);
        }
        
        /// <summary>
        /// 检查时间线信号量是否已达到特定值
        /// </summary>
        public bool IsValueSignaled(ulong targetValue)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0)
                return true;
                
            ulong currentValue = _gd.GetTimelineSemaphoreValue();
            return currentValue >= targetValue;
        }
        
        /// <summary>
        /// 批量检查多个时间线信号量值
        /// </summary>
        public bool AreValuesSignaled(ulong[] targetValues)
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0 || targetValues == null)
                return true;
                
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
        /// 清理指定命令缓冲区的等待器
        /// </summary>
        public void ClearBuffer(int cbIndex)
        {
            if (_holderMap.TryRemove(cbIndex, out var holder))
            {
                holder.Clear();
            }
        }
        
        /// <summary>
        /// 清理所有等待器
        /// </summary>
        public void ClearAll()
        {
            _mainHolder.Clear();
            
            foreach (var kvp in _holderMap)
            {
                kvp.Value.Clear();
            }
            _holderMap.Clear();
            
            lock (_pendingLock)
            {
                _pendingValues.Clear();
            }
        }
        
        /// <summary>
        /// 获取当前时间线信号量的值
        /// </summary>
        public ulong GetCurrentTimelineValue()
        {
            if (!_gd.SupportsTimelineSemaphores || _timelineSemaphore.Handle == 0)
                return 0;
                
            return _gd.GetTimelineSemaphoreValue();
        }
        
        public void Dispose()
        {
            if (_disposed)
                return;
                
            _disposed = true;
            
            _flushTimer?.Dispose();
            _flushTimer = null;
            
            ClearAll();
            
            lock (_instanceLock)
            {
                _instance = null;
            }
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"TimelineFenceHolderPool已销毁");
        }
    }
}