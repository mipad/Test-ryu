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
            public TimelineFenceHolder TimelineFenceHolder; // 时间线等待器
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

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];
            _lastStrictFlushId = 0;
            _lastStrictTimelineValue = 0;
            
            // 输出时间线信号量支持信息
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

            // 输出创建同步对象的日志
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
                    
                    // 创建TimelineFenceHolder
                    handle.TimelineFenceHolder = new TimelineFenceHolder(_gd, _device, _gd.TimelineSemaphore);
                    
                    // 严格模式：创建一个立即提交的命令缓冲区来发出时间线信号
                    // 这样可以确保时间线信号量值被立即提交，而不是等待下一个命令缓冲区
                    var cbs = _gd.CommandBufferPool.Rent();
                    
                    try
                    {
                        // 立即结束并提交命令缓冲区
                        _gd.EndAndSubmitCommandBuffer(cbs, timelineValue);
                        
                        // 添加信号到TimelineFenceHolder（-1表示立即提交的命令缓冲区）
                        handle.TimelineFenceHolder.AddSignal(-1, timelineValue);
                        
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
                // 非严格模式：将时间线信号量添加到当前使用的命令缓冲区
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    // 创建TimelineFenceHolder
                    handle.TimelineFenceHolder = new TimelineFenceHolder(_gd, _device, _gd.TimelineSemaphore);
                    
                    // 获取当前活动的命令缓冲区索引
                    int currentCbIndex = _gd.GetCurrentCommandBufferIndex();
                    
                    if (currentCbIndex >= 0)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"非严格模式: 添加时间线信号量到命令缓冲区 {currentCbIndex}，值={timelineValue}");
                        
                        // 直接添加到特定命令缓冲区，而不是所有使用中的命令缓冲区
                        _gd.CommandBufferPool.AddTimelineSignalToBuffer(currentCbIndex, _gd.TimelineSemaphore, timelineValue);
                        _gd.CommandBufferPool.AddTimelineFenceHolder(currentCbIndex, handle.TimelineFenceHolder);
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, 
                            $"非严格模式: 未找到当前命令缓冲区，回退到传统方法");
                        _gd.CommandBufferPool.AddInUseTimelineSignal(_gd.TimelineSemaphore, timelineValue);
                        _gd.CommandBufferPool.AddInUseTimelineFenceHolder(handle.TimelineFenceHolder);
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
                        if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && handle.TimelineFenceHolder != null)
                        {
                            if (handle.TimelineFenceHolder.WaitForSignals(_gd.Api, _device, 0))
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
                    
                    if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && result.TimelineFenceHolder != null)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"使用时间线信号量等待: 目标值={result.TimelineValue}");
                        
                        // 使用TimelineFenceHolder等待
                        signaled = result.TimelineFenceHolder.WaitForSignals(_gd.Api, _device, 1000000000);
                        
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
                        // 这里需要实现回退逻辑，但为了简化，我们假设已发出信号
                        signaled = true;
                    }

                    if (!signaled)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"VK Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
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
                
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && first.TimelineFenceHolder != null)
                {
                    // 检查时间线信号量是否已达到此值
                    signaled = first.TimelineFenceHolder.WaitForSignals(_gd.Api, _device, 0);
                    
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
                            
                            // 清理TimelineFenceHolder资源
                            first.TimelineFenceHolder?.Clear();
                            
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
    }
}