using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    class SyncManager
    {
        private class SyncHandle
        {
            public ulong ID;
            public ulong TimelineValue; // 时间线信号量值
            public ulong FlushId;
            public bool Signalled;
            public MultiFenceHolder MultiFenceHolder; // 传统的MultiFenceHolder（用于不支持时间线信号量的情况）

            public bool NeedsFlush(ulong currentFlushId)
            {
                return (long)(FlushId - currentFlushId) >= 0;
            }
        }

        private ulong _firstHandle;
        private ulong _nextTimelineValue = 1; // 时间线信号量值从1开始

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly List<SyncHandle> _handles;
        private ulong _flushId;
        private long _waitTicks;
        
        // 用于跟踪最后一次严格模式的提交
        private ulong _lastStrictFlushId;
        private ulong _lastStrictTimelineValue;
        
        // 时间线等待器池
        private TimelineFenceHolderPool _timelinePool;
        private TimelineFenceHolder _sharedTimelineHolder;

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];
            _lastStrictFlushId = 0;
            _lastStrictTimelineValue = 0;
            
            // 初始化时间线等待器池
            if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
            {
                _timelinePool = TimelineFenceHolderPool.GetInstance(_gd, _device, _gd.TimelineSemaphore);
                _sharedTimelineHolder = _timelinePool.GetMainHolder();
            }
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"SyncManager初始化: 时间线信号量支持 = {_gd.SupportsTimelineSemaphores}");
        }

        public void RegisterFlush()
        {
            _flushId++;
        }

        public void Create(ulong id, bool strict)
        {
            ulong flushId = _flushId;
            ulong timelineValue = _nextTimelineValue++;

            SyncHandle handle = new()
            {
                ID = id,
                TimelineValue = timelineValue,
                FlushId = flushId,
            };

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"创建同步对象 ID={id}, TimelineValue={timelineValue}, FlushId={flushId}, Strict={strict}");

            if (strict || _gd.InterruptAction == null)
            {
                // 检查是否是重复的严格模式提交
                if (strict && flushId == _lastStrictFlushId && timelineValue == _lastStrictTimelineValue + 1)
                {
                    Logger.Warning?.PrintMsg(LogClass.Gpu, 
                        $"检测到可能的重复严格模式提交: FlushId={flushId}, TimelineValue={timelineValue}");
                }
                
                _lastStrictFlushId = flushId;
                _lastStrictTimelineValue = timelineValue;
                
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"严格模式: 刷新所有命令并提交时间线信号量值={timelineValue}");
                
                // 严格模式下，立即刷新所有命令
                _gd.FlushAllCommands();
                
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"严格模式: 创建立即提交的时间线信号量值={timelineValue}");
                    
                    // 使用时间线等待器池添加信号
                    _timelinePool.AddSignal(timelineValue);
                    
                    // 严格模式：创建一个立即提交的命令缓冲区来发出时间线信号
                    var cbs = _gd.CommandBufferPool.Rent();
                    
                    try
                    {
                        _gd.EndAndSubmitCommandBuffer(cbs, timelineValue);
                        
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"严格模式: 命令缓冲区已立即提交，时间线信号量值={timelineValue}");
                    }
                    finally
                    {
                        // 注意：不需要手动Return，因为EndAndSubmitCommandBuffer已经处理了
                    }
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"严格模式: 时间线信号量不支持，使用栅栏回退机制");
                    // 回退到旧的栅栏机制
                    handle.MultiFenceHolder = new MultiFenceHolder();
                    _gd.CommandBufferPool.AddWaitable(handle.MultiFenceHolder);
                }
            }
            else
            {
                // 非严格模式
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    // 使用时间线等待器池添加信号
                    _timelinePool.AddSignal(timelineValue);
                    
                    // 获取当前活动的命令缓冲区索引
                    int currentCbIndex = _gd.GetCurrentCommandBufferIndex();
                    
                    if (currentCbIndex >= 0)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"非严格模式: 添加时间线信号量到命令缓冲区 {currentCbIndex}，值={timelineValue}");
                        
                        // 直接添加到特定命令缓冲区
                        _gd.CommandBufferPool.AddTimelineSignalToBuffer(currentCbIndex, _gd.TimelineSemaphore, timelineValue);
                        
                        // 同时添加到池中该缓冲区的等待器
                        _timelinePool.AddSignalToBuffer(currentCbIndex, timelineValue);
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"非严格模式: 未找到当前命令缓冲区，使用延迟提交");
                        // 延迟提交，等待器池会自动处理
                    }
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"非严格模式: 时间线信号量不支持，使用栅栏回退机制");
                    // 回退到旧的栅栏机制
                    handle.MultiFenceHolder = new MultiFenceHolder();
                    _gd.CommandBufferPool.AddInUseWaitable(handle.MultiFenceHolder);
                }
            }

            lock (_handles)
            {
                _handles.Add(handle);
            }
        }

        public ulong GetCurrent()
        {
            lock (_handles)
            {
                ulong lastHandle = _firstHandle;

                foreach (SyncHandle handle in _handles)
                {
                    lock (handle)
                    {
                        if (handle.Signalled)
                        {
                            if (handle.ID > lastHandle)
                            {
                                lastHandle = handle.ID;
                            }
                            continue;
                        }

                        // 检查时间线信号量是否已达到此值
                        if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && 
                            handle.TimelineValue > 0)
                        {
                            if (_timelinePool.IsValueSignaled(handle.TimelineValue))
                            {
                                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                    $"同步对象 ID={handle.ID} 已发出信号，时间线值={handle.TimelineValue}");
                                lastHandle = handle.ID;
                                handle.Signalled = true;
                            }
                        }
                        else if (handle.MultiFenceHolder != null)
                        {
                            // 使用传统的MultiFenceHolder检查
                            bool signaled = handle.Signalled || handle.MultiFenceHolder.WaitForFences(_gd.Api, _device, 0);
                            if (signaled)
                            {
                                lastHandle = handle.ID;
                                handle.Signalled = true;
                            }
                        }
                        else
                        {
                            // 回退：我们无法查询当前值，返回最后一个已知的
                        }
                    }
                }

                return lastHandle;
            }
        }

        public void Wait(ulong id)
        {
            SyncHandle result = null;

            lock (_handles)
            {
                if ((long)(_firstHandle - id) > 0)
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象 ID={id} 已发出信号或已删除，无需等待");
                    return; // 句柄已发出信号或已删除
                }

                foreach (SyncHandle handle in _handles)
                {
                    if (handle.ID == id)
                    {
                        result = handle;
                        break;
                    }
                }
            }

            if (result != null)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"开始等待同步对象 ID={result.ID}, TimelineValue={result.TimelineValue}");

                long beforeTicks = Stopwatch.GetTimestamp();

                if (result.NeedsFlush(_flushId))
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象需要刷新，当前FlushId={_flushId}，对象FlushId={result.FlushId}");
                    _gd.InterruptAction(() =>
                    {
                        if (result.NeedsFlush(_flushId))
                        {
                            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                $"中断GPU线程并刷新所有命令");
                            _gd.FlushAllCommands();
                        }
                    });
                }

                lock (result)
                {
                    if (result.Signalled)
                    {
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"同步对象 ID={result.ID} 已发出信号，无需等待");
                        return;
                    }

                    bool signaled = false;
                    
                    if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && 
                        result.TimelineValue > 0)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"使用时间线信号量等待: 目标值={result.TimelineValue}");
                        
                        // 使用时间线等待器池等待
                        signaled = _timelinePool.WaitForValue(result.TimelineValue, 1000000000);
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"等待结果: {signaled}");
                    }
                    else if (result.MultiFenceHolder != null)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            "使用传统MultiFenceHolder等待");
                        
                        // 使用传统的MultiFenceHolder等待
                        signaled = result.Signalled || result.MultiFenceHolder.WaitForFences(_gd.Api, _device, 1000000000);
                    }
                    else
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            "时间线信号量不支持，使用回退栅栏等待机制");
                        
                        // 回退到等待栅栏
                        signaled = true;
                    }

                    if (!signaled)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu, 
                            $"VK Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
                    }
                    else
                    {
                        _waitTicks += Stopwatch.GetTimestamp() - beforeTicks;
                        result.Signalled = true;
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"同步对象 ID={result.ID} 等待成功，耗时={Stopwatch.GetTimestamp() - beforeTicks} ticks");
                    }
                }
            }
            else
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, 
                    $"等待同步对象 ID={id} 未找到");
            }
        }

        public void Cleanup()
        {
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"开始清理同步对象，当前句柄数量={_handles.Count}");
            
            // 迭代句柄并删除任何已发出信号的句柄
            while (true)
            {
                SyncHandle first = null;
                lock (_handles)
                {
                    first = _handles.FirstOrDefault();
                }

                if (first == null || first.NeedsFlush(_flushId))
                {
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"清理完成或需要刷新，当前FlushId={_flushId}");
                    break;
                }

                bool signaled = false;
                
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && 
                    first.TimelineValue > 0)
                {
                    // 检查时间线信号量是否已达到此值
                    signaled = _timelinePool.IsValueSignaled(first.TimelineValue);
                    
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"检查同步对象 ID={first.ID}: 已发出信号={signaled}");
                }
                else if (first.MultiFenceHolder != null)
                {
                    // 使用传统的MultiFenceHolder检查
                    signaled = first.MultiFenceHolder.WaitForFences(_gd.Api, _device, 0);
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"检查同步对象 ID={first.ID} (传统): 已发出信号={signaled}");
                }
                else
                {
                    // 回退：我们无法检查，假设已发出信号
                    signaled = true;
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"时间线信号量不支持，假设同步对象 ID={first.ID} 已发出信号");
                }

                if (signaled)
                {
                    // 删除同步对象
                    lock (_handles)
                    {
                        lock (first)
                        {
                            _firstHandle = first.ID + 1;
                            _handles.RemoveAt(0);
                            
                            // 清理MultiFenceHolder资源
                            if (first.MultiFenceHolder != null)
                            {
                                Array.Clear(first.MultiFenceHolder.Fences);
                                MultiFenceHolder.FencePool.Release(first.MultiFenceHolder.Fences);
                                first.MultiFenceHolder = null;
                            }
                            
                            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                $"删除同步对象 ID={first.ID}，新的_firstHandle={_firstHandle}");
                        }
                    }
                }
                else
                {
                    // 此同步句柄及后续的尚未到达
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象 ID={first.ID} 尚未发出信号，停止清理");
                    break;
                }
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"清理完成，剩余句柄数量={_handles.Count}");
        }

        public long GetAndResetWaitTicks()
        {
            long result = _waitTicks;
            _waitTicks = 0;

            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"获取并重置等待ticks: {result}");
            
            return result;
        }
        
        /// <summary>
        /// 获取当前时间线信号量的值
        /// </summary>
        public ulong GetCurrentTimelineValue()
        {
            if (!_gd.SupportsTimelineSemaphores || _gd.TimelineSemaphore.Handle == 0)
            {
                return 0;
            }
            
            return _gd.GetTimelineSemaphoreValue();
        }
    }
}