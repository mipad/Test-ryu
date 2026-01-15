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
            public MultiFenceHolder Waitable;
            public ulong TimelineValue; // 时间线信号量值
            public bool UseTimeline;    // 是否使用时间线信号量
            public ulong FlushId;
            public bool Signalled;

            public bool NeedsFlush(ulong currentFlushId)
            {
                return (long)(FlushId - currentFlushId) >= 0;
            }
        }

        private ulong _firstHandle;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly List<SyncHandle> _handles;
        private ulong _flushId;
        private long _waitTicks;
        
        // 时间线信号量支持
        private readonly bool _supportsTimelineSemaphores;
        private ulong _nextTimelineValue = 1; // 时间线信号量值从1开始

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];
            _supportsTimelineSemaphores = gd.SupportsTimelineSemaphores;
            
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"SyncManager初始化: 时间线信号量支持 = {_supportsTimelineSemaphores}");
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
            };

            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"创建同步对象 ID={id}, FlushId={flushId}, Strict={strict}");

            if (_supportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
            {
                // 使用时间线信号量
                handle.UseTimeline = true;
                handle.TimelineValue = _nextTimelineValue++;
                
                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"使用时间线信号量: TimelineValue={handle.TimelineValue}");

                if (strict || _gd.InterruptAction == null)
                {
                    // 严格模式：立即刷新所有命令并提交
                    _gd.FlushAllCommands();
                    
                    // 创建立即提交的命令缓冲区来发出时间线信号
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"严格模式: 创建立即提交的时间线信号量值={handle.TimelineValue}");
                    
                    // 获取当前命令缓冲区
                    var cbs = _gd.CommandBufferPool.Rent();
                    
                    try
                    {
                        // 立即结束并提交命令缓冲区
                        _gd.EndAndSubmitCommandBuffer(cbs, handle.TimelineValue);
                        
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"严格模式: 命令缓冲区已立即提交，时间线信号量值={handle.TimelineValue}");
                    }
                    finally
                    {
                        // 注意：不需要手动Return，因为EndAndSubmitCommandBuffer已经处理了
                    }
                }
                else
                {
                    // 非严格模式：将时间线信号量添加到当前使用的命令缓冲区
                    Logger.Info?.PrintMsg(LogClass.Gpu, 
                        $"非严格模式: 添加时间线信号量值={handle.TimelineValue} 到使用中的命令缓冲区");
                    
                    // 添加到所有使用中的命令缓冲区
                    _gd.CommandBufferPool.AddInUseTimelineSignal(_gd.TimelineSemaphore, handle.TimelineValue);
                }
            }
            else
            {
                // 回退到栅栏机制
                handle.UseTimeline = false;
                MultiFenceHolder waitable = new();
                handle.Waitable = waitable;

                if (strict || _gd.InterruptAction == null)
                {
                    _gd.FlushAllCommands();
                    _gd.CommandBufferPool.AddWaitable(waitable);
                }
                else
                {
                    // 不要刷新命令，而是等待当前命令缓冲区完成。
                    // 如果在此命令缓冲区提交之前等待此同步，则中断GPU线程并手动刷新。
                    _gd.CommandBufferPool.AddInUseWaitable(waitable);
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
                        if (handle.Waitable == null && !handle.UseTimeline)
                        {
                            continue;
                        }

                        if (handle.ID > lastHandle)
                        {
                            bool signaled = false;
                            
                            if (handle.UseTimeline)
                            {
                                // 检查时间线信号量是否已达到此值
                                ulong currentValue = _gd.GetTimelineSemaphoreValue();
                                signaled = currentValue >= handle.TimelineValue;
                                
                                if (signaled)
                                {
                                    Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                        $"同步对象 ID={handle.ID} 已发出信号，时间线值={handle.TimelineValue}，当前值={currentValue}");
                                }
                            }
                            else
                            {
                                signaled = handle.Signalled || handle.Waitable.WaitForFences(_gd.Api, _device, 0);
                            }
                            
                            if (signaled)
                            {
                                lastHandle = handle.ID;
                                handle.Signalled = true;
                            }
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
                if (result.Waitable == null && !result.UseTimeline)
                {
                    return;
                }

                Logger.Info?.PrintMsg(LogClass.Gpu, 
                    $"开始等待同步对象 ID={result.ID}" + 
                    (result.UseTimeline ? $", TimelineValue={result.TimelineValue}" : ""));

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
                    
                    if (result.UseTimeline)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, 
                            $"使用时间线信号量等待: 目标值={result.TimelineValue}");
                        
                        // 等待时间线信号量达到特定值
                        unsafe
                        {
                            var timelineSemaphore = _gd.TimelineSemaphore;
                            var waitValue = result.TimelineValue;
                            
                            if (timelineSemaphore.Handle == 0)
                            {
                                Logger.Error?.PrintMsg(LogClass.Gpu, 
                                    $"时间线信号量无效，无法等待");
                                signaled = false;
                            }
                            else
                            {
                                var waitInfo = new SemaphoreWaitInfo
                                {
                                    SType = StructureType.SemaphoreWaitInfo,
                                    SemaphoreCount = 1,
                                    PSemaphores = &timelineSemaphore,
                                    PValues = &waitValue
                                };

                                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                    $"调用vkWaitSemaphores，超时=1秒");
                                
                                var resultCode = _gd.Api.WaitSemaphores(
                                    _device, 
                                    &waitInfo, 
                                    1000000000 // 1秒超时
                                );
                                
                                signaled = resultCode == Result.Success;
                                
                                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                                    $"vkWaitSemaphores 结果: {resultCode}, 成功={signaled}");
                                
                                if (!signaled)
                                {
                                    // 检查当前值，提供更多调试信息
                                    ulong currentValue = 0;
                                    _gd.Api.GetSemaphoreCounterValue(_device, timelineSemaphore, &currentValue);
                                    Logger.Warning?.PrintMsg(LogClass.Gpu, 
                                        $"等待超时: 当前时间线值={currentValue}, 目标值={waitValue}");
                                }
                            }
                        }
                    }
                    else if (result.Waitable != null)
                    {
                        signaled = result.Signalled || result.Waitable.WaitForFences(_gd.Api, _device, 1000000000);
                    }

                    if (!signaled)
                    {
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"VK Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
                        // 超时后强制标记为已发出信号，避免死锁
                        signaled = true;
                    }
                    
                    if (signaled)
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
                
                if (first.UseTimeline)
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
                            
                            if (!first.UseTimeline && first.Waitable != null)
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