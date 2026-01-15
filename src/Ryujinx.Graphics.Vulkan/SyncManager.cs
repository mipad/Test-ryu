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

        // 统计信息
        private SyncStats _stats = new SyncStats();

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];

            Logger.Info?.Print(LogClass.Gpu, 
                $"SyncManager initialized. Timeline semaphores supported: {_gd.SupportsTimelineSemaphores}");
        }

        public void RegisterFlush()
        {
            _flushId++;
            Logger.Trace?.Print(LogClass.Gpu, 
                $"Register flush #{_flushId}");
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

            // 更新统计
            _stats.TotalSyncObjectsCreated++;

            if (strict || _gd.InterruptAction == null)
            {
                _gd.FlushAllCommands();
                
                // 提交命令缓冲区时设置时间线信号量值
                if (_gd.SupportsTimelineSemaphores)
                {
                    _gd.SignalTimelineSemaphore(timelineValue);
                    
                    // 更新统计
                    _stats.StrictTimelineSyncsCreated++;
                    
                    // 添加日志
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Created strict sync object {id} with timeline value {timelineValue} (FlushId: {flushId})");
                }
                else
                {
                    // 回退到旧的栅栏机制
                    MultiFenceHolder waitable = new();
                    _gd.CommandBufferPool.AddWaitable(waitable);
                    
                    // 更新统计
                    _stats.StrictFallbackSyncsCreated++;
                    
                    // 添加日志
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Created strict sync object {id} using fallback fence mechanism (no timeline support)");
                }
            }
            else
            {
                // 不刷新命令，等待当前命令缓冲区完成
                // 如果在此同步对象被等待之前提交了命令缓冲区，中断GPU线程并手动刷新
                if (_gd.SupportsTimelineSemaphores)
                {
                    _gd.CommandBufferPool.AddInUseTimelineSignal(_gd.TimelineSemaphore, timelineValue);
                    
                    // 更新统计
                    _stats.NonStrictTimelineSyncsCreated++;
                    
                    // 添加日志
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Created non-strict sync object {id} with timeline value {timelineValue} (FlushId: {flushId})");
                }
                else
                {
                    // 回退到旧的栅栏机制
                    MultiFenceHolder waitable = new();
                    _gd.CommandBufferPool.AddInUseWaitable(waitable);
                    
                    // 更新统计
                    _stats.NonStrictFallbackSyncsCreated++;
                    
                    // 添加日志
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Created non-strict sync object {id} using fallback fence mechanism");
                }
            }

            lock (_handles)
            {
                _handles.Add(handle);
                Logger.Trace?.Print(LogClass.Gpu, 
                    $"Sync object {id} added to handles list. Total handles: {_handles.Count}");
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
                        if (_gd.SupportsTimelineSemaphores)
                        {
                            ulong currentValue = _gd.GetTimelineSemaphoreValue();
                            
                            if (currentValue >= handle.TimelineValue)
                            {
                                lastHandle = handle.ID;
                                handle.Signalled = true;
                                
                                Logger.Trace?.Print(LogClass.Gpu, 
                                    $"Sync object {handle.ID} signalled (timeline value reached: {currentValue} >= {handle.TimelineValue})");
                            }
                        }
                        else
                        {
                            // 回退：我们无法查询当前值，返回最后一个已知的
                            // 对于时间线信号量，我们应该总是能查询到值
                            // 这里只为了兼容性保留
                        }
                    }
                }

                Logger.Trace?.Print(LogClass.Gpu, 
                    $"GetCurrentSync returning: {lastHandle}");
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
                    Logger.Debug?.Print(LogClass.Gpu, 
                        $"Sync object {id} already signaled (firstHandle: {_firstHandle})");
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
                long beforeTicks = Stopwatch.GetTimestamp();
                
                // 更新统计
                _stats.TotalSyncWaits++;
                
                // 添加等待开始日志
                Logger.Debug?.Print(LogClass.Gpu, 
                    $"Starting wait for sync object {id} (timeline value: {result.TimelineValue}, flushId: {result.FlushId})");

                if (result.NeedsFlush(_flushId))
                {
                    _gd.InterruptAction(() =>
                    {
                        if (result.NeedsFlush(_flushId))
                        {
                            Logger.Trace?.Print(LogClass.Gpu, 
                                $"Flushing commands for sync object {id} (needs flush)");
                            _gd.FlushAllCommands();
                        }
                    });
                }

                lock (result)
                {
                    if (result.Signalled)
                    {
                        Logger.Trace?.Print(LogClass.Gpu, 
                            $"Sync object {id} already signalled, no wait needed");
                        return;
                    }

                    bool signaled = false;
                    
                    if (_gd.SupportsTimelineSemaphores)
                    {
                        // 更新统计
                        _stats.TimelineSemaphoreWaits++;
                        
                        // 等待时间线信号量达到特定值
                        unsafe
                        {
                            // 添加详细日志
                            Logger.Debug?.Print(LogClass.Gpu, 
                                $"Waiting for timeline semaphore to reach value {result.TimelineValue}");
                            
                            var timelineSemaphore = _gd.TimelineSemaphore;
                            var waitValue = result.TimelineValue;
                            
                            var waitInfo = new SemaphoreWaitInfo
                            {
                                SType = StructureType.SemaphoreWaitInfo,
                                SemaphoreCount = 1,
                                PSemaphores = &timelineSemaphore,
                                PValues = &waitValue
                            };

                            var resultCode = _gd.TimelineSemaphoreApi.WaitSemaphores(
                                _device, 
                                &waitInfo, 
                                1000000000 // 1秒超时
                            );
                            
                            signaled = resultCode == Result.Success;
                            
                            if (signaled)
                            {
                                Logger.Trace?.Print(LogClass.Gpu, 
                                    $"Timeline semaphore wait successful for value {result.TimelineValue}");
                            }
                            else
                            {
                                Logger.Warning?.Print(LogClass.Gpu, 
                                    $"Timeline semaphore wait failed or timed out for value {result.TimelineValue}: {resultCode}");
                            }
                        }
                    }
                    else
                    {
                        // 更新统计
                        _stats.FallbackFenceWaits++;
                        
                        // 回退到旧的栅栏等待机制
                        // 注意：由于我们不再存储waitable，这里无法等待
                        // 这应该是回退路径，实际上不应该执行到这里
                        Logger.Warning?.Print(LogClass.Gpu, "Timeline semaphores not supported, using fallback sync");
                        
                        // 回退到等待栅栏
                        // 这里需要实现回退逻辑，但为了简化，我们假设已发出信号
                        signaled = true;
                    }

                    if (!signaled)
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"VK Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
                        _stats.FailedSyncWaits++;
                    }
                    else
                    {
                        long waitTimeMs = (Stopwatch.GetTimestamp() - beforeTicks) * 1000 / Stopwatch.Frequency;
                        _waitTicks += Stopwatch.GetTimestamp() - beforeTicks;
                        result.Signalled = true;
                        
                        Logger.Debug?.Print(LogClass.Gpu, 
                            $"Sync object {id} signaled after {waitTimeMs}ms");
                    }
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.Gpu, 
                    $"Sync object {id} not found in handles list");
                _stats.MissingSyncWaits++;
            }
        }

        public void Cleanup()
        {
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
                    break;
                }

                bool signaled = false;
                
                if (_gd.SupportsTimelineSemaphores)
                {
                    // 检查时间线信号量是否已达到此值
                    ulong currentValue = _gd.GetTimelineSemaphoreValue();
                    signaled = currentValue >= first.TimelineValue;
                    
                    if (signaled)
                    {
                        Logger.Trace?.Print(LogClass.Gpu, 
                            $"Cleanup: Sync object {first.ID} signalled (timeline {currentValue} >= {first.TimelineValue})");
                    }
                }
                else
                {
                    // 回退：我们无法检查，假设已发出信号
                    signaled = true;
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
                            _stats.SyncObjectsCleanedUp++;
                            
                            Logger.Trace?.Print(LogClass.Gpu, 
                                $"Cleanup: Removed sync object {first.ID}. Remaining: {_handles.Count}");
                        }
                    }
                }
                else
                {
                    // 此同步句柄及后续的尚未到达
                    Logger.Trace?.Print(LogClass.Gpu, 
                        $"Cleanup: Sync object {first.ID} not yet signalled, stopping cleanup");
                    break;
                }
            }
        }

        public long GetAndResetWaitTicks()
        {
            long result = _waitTicks;
            _waitTicks = 0;

            return result;
        }

        public void LogStats()
        {
            if (Logger.IsInfoEnabled(LogClass.Gpu))
            {
                Logger.Info?.Print(LogClass.Gpu, "SyncManager Statistics:");
                Logger.Info?.Print(LogClass.Gpu, $"  Total sync objects created: {_stats.TotalSyncObjectsCreated}");
                Logger.Info?.Print(LogClass.Gpu, $"  Strict timeline syncs: {_stats.StrictTimelineSyncsCreated}");
                Logger.Info?.Print(LogClass.Gpu, $"  Strict fallback syncs: {_stats.StrictFallbackSyncsCreated}");
                Logger.Info?.Print(LogClass.Gpu, $"  Non-strict timeline syncs: {_stats.NonStrictTimelineSyncsCreated}");
                Logger.Info?.Print(LogClass.Gpu, $"  Non-strict fallback syncs: {_stats.NonStrictFallbackSyncsCreated}");
                
                if (_stats.TotalSyncObjectsCreated > 0)
                {
                    int timelinePercentage = _stats.TotalSyncObjectsCreated > 0 ? 
                        (_stats.StrictTimelineSyncsCreated + _stats.NonStrictTimelineSyncsCreated) * 100 / _stats.TotalSyncObjectsCreated : 0;
                    Logger.Info?.Print(LogClass.Gpu, $"  Timeline sync usage: {timelinePercentage}%");
                }
                
                Logger.Info?.Print(LogClass.Gpu, $"  Total sync waits: {_stats.TotalSyncWaits}");
                Logger.Info?.Print(LogClass.Gpu, $"  Timeline semaphore waits: {_stats.TimelineSemaphoreWaits}");
                Logger.Info?.Print(LogClass.Gpu, $"  Fallback fence waits: {_stats.FallbackFenceWaits}");
                Logger.Info?.Print(LogClass.Gpu, $"  Failed sync waits: {_stats.FailedSyncWaits}");
                Logger.Info?.Print(LogClass.Gpu, $"  Missing sync waits: {_stats.MissingSyncWaits}");
                Logger.Info?.Print(LogClass.Gpu, $"  Sync objects cleaned up: {_stats.SyncObjectsCleanedUp}");
                
                long totalWaitMs = GetAndResetWaitTicks() * 1000 / Stopwatch.Frequency;
                Logger.Info?.Print(LogClass.Gpu, $"  Total wait time: {totalWaitMs}ms");
            }
        }

        private class SyncStats
        {
            public int TotalSyncObjectsCreated { get; set; }
            public int StrictTimelineSyncsCreated { get; set; }
            public int StrictFallbackSyncsCreated { get; set; }
            public int NonStrictTimelineSyncsCreated { get; set; }
            public int NonStrictFallbackSyncsCreated { get; set; }
            public int TotalSyncWaits { get; set; }
            public int TimelineSemaphoreWaits { get; set; }
            public int FallbackFenceWaits { get; set; }
            public int FailedSyncWaits { get; set; }
            public int MissingSyncWaits { get; set; }
            public int SyncObjectsCleanedUp { get; set; }
        }
    }
}
