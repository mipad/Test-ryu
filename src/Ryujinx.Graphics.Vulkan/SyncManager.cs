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
            public ulong TimelineValue; // 时间线信号量值（仅用于严格模式）
            public MultiFenceHolder Waitable; // 传统的MultiFenceHolder（用于非严格模式）
            public ulong FlushId;
            public bool Signalled;
            public bool IsStrictMode; // 标记是否为严格模式

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

            SyncHandle handle = new()
            {
                ID = id,
                FlushId = flushId,
                IsStrictMode = strict
            };

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"创建同步对象 ID={id}, Strict={strict}, 时间线信号量支持={_gd.SupportsTimelineSemaphores}");

            if (strict || _gd.InterruptAction == null)
            {
                // 严格模式：使用时间线信号量
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    ulong timelineValue = _nextTimelineValue++;
                    handle.TimelineValue = timelineValue;

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
                    
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"严格模式: 创建立即提交的时间线信号量值={timelineValue}");
                    
                    // 严格模式：创建一个立即提交的命令缓冲区来发出时间线信号
                    var cbs = _gd.CommandBufferPool.Rent();
                    
                    try
                    {
                        // 立即结束并提交命令缓冲区
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
                    handle.Waitable = new MultiFenceHolder();
                    _gd.CommandBufferPool.AddWaitable(handle.Waitable);
                }
            }
            else
            {
                // 非严格模式：使用传统的MultiFenceHolder
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"非严格模式: 使用栅栏机制");
                
                handle.Waitable = new MultiFenceHolder();
                _gd.CommandBufferPool.AddInUseWaitable(handle.Waitable);
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

                        bool signaled = false;
                        
                        if (handle.IsStrictMode && _gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                        {
                            // 严格模式：检查时间线信号量值
                            ulong currentValue = _gd.GetTimelineSemaphoreValue();
                            
                            if (currentValue >= handle.TimelineValue)
                            {
                                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                    $"同步对象 ID={handle.ID} 已发出信号，时间线值={handle.TimelineValue}，当前值={currentValue}");
                                signaled = true;
                            }
                        }
                        else if (handle.Waitable != null)
                        {
                            // 非严格模式：检查栅栏
                            signaled = handle.Waitable.WaitForFences(_gd.Api, _device, 0);
                        }

                        if (signaled)
                        {
                            lastHandle = handle.ID;
                            handle.Signalled = true;
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
                    $"开始等待同步对象 ID={result.ID}, Strict={result.IsStrictMode}, TimelineValue={result.TimelineValue}");

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
                    
                    if (result.IsStrictMode && _gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"使用时间线信号量等待: 目标值={result.TimelineValue}");
                        
                        // 等待时间线信号量达到特定值
                        unsafe
                        {
                            var timelineSemaphore = _gd.TimelineSemaphore;
                            var waitValue = result.TimelineValue;
                            
                            // 分配栈上内存
                            Semaphore* pSemaphore = stackalloc Semaphore[1];
                            ulong* pValue = stackalloc ulong[1];
                            *pSemaphore = timelineSemaphore;
                            *pValue = waitValue;
                            
                            var waitInfo = new SemaphoreWaitInfo
                            {
                                SType = StructureType.SemaphoreWaitInfo,
                                SemaphoreCount = 1,
                                PSemaphores = pSemaphore,
                                PValues = pValue
                            };

                            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                $"调用vkWaitSemaphores，超时=1秒");
                            
                            var resultCode = _gd.TimelineSemaphoreApi.WaitSemaphores(
                                _device, 
                                &waitInfo, 
                                1000000000 // 1秒超时
                            );
                            
                            signaled = resultCode == Result.Success;
                            
                            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                $"vkWaitSemaphores 结果: {resultCode}, 成功={signaled}");
                        }
                    }
                    else if (result.Waitable != null)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            "使用传统栅栏等待机制");
                        
                        signaled = result.Waitable.WaitForFences(_gd.Api, _device, 1000000000);
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, "同步对象既没有时间线信号量也没有栅栏");
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
                
                if (first.IsStrictMode && _gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    // 检查时间线信号量是否已达到此值
                    ulong currentValue = _gd.GetTimelineSemaphoreValue();
                    signaled = currentValue >= first.TimelineValue;
                    
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"检查同步对象 ID={first.ID}: 当前时间线值={currentValue}, 目标值={first.TimelineValue}, 已发出信号={signaled}");
                }
                else if (first.Waitable != null)
                {
                    signaled = first.Waitable.WaitForFences(_gd.Api, _device, 0);
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"检查同步对象 ID={first.ID}: 使用栅栏检查，已发出信号={signaled}");
                }
                else
                {
                    signaled = true;
                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                        $"同步对象 ID={first.ID} 没有同步机制，假设已发出信号");
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
                            if (first.Waitable != null)
                            {
                                Array.Clear(first.Waitable.Fences);
                                MultiFenceHolder.FencePool.Release(first.Waitable.Fences);
                                first.Waitable = null;
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