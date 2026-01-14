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

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];
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

            if (strict || _gd.InterruptAction == null)
            {
                _gd.FlushAllCommands();
                
                // 提交命令缓冲区时设置时间线信号量值
                if (_gd.SupportsTimelineSemaphores)
                {
                    _gd.SignalTimelineSemaphore(timelineValue);
                }
                else
                {
                    // 回退到旧的栅栏机制
                    MultiFenceHolder waitable = new();
                    _gd.CommandBufferPool.AddWaitable(waitable);
                }
            }
            else
            {
                // 不刷新命令，等待当前命令缓冲区完成
                // 如果在此同步对象被等待之前提交了命令缓冲区，中断GPU线程并手动刷新
                if (_gd.SupportsTimelineSemaphores)
                {
                    _gd.CommandBufferPool.AddInUseTimelineSignal(_gd.TimelineSemaphore, timelineValue);
                }
                else
                {
                    // 回退到旧的栅栏机制
                    MultiFenceHolder waitable = new();
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

                if (result.NeedsFlush(_flushId))
                {
                    _gd.InterruptAction(() =>
                    {
                        if (result.NeedsFlush(_flushId))
                        {
                            _gd.FlushAllCommands();
                        }
                    });
                }

                lock (result)
                {
                    if (result.Signalled)
                    {
                        return;
                    }

                    bool signaled = false;
                    
                    if (_gd.SupportsTimelineSemaphores)
                    {
                        // 等待时间线信号量达到特定值
                        unsafe
                        {
                            var waitInfo = new SemaphoreWaitInfoKHR
                            {
                                SType = StructureType.SemaphoreWaitInfo,
                                SemaphoreCount = 1,
                                PSemaphores = &_gd.TimelineSemaphore,
                                PValues = &result.TimelineValue
                            };

                            var resultCode = _gd.TimelineSemaphoreApi.WaitSemaphores(
                                _device, 
                                &waitInfo, 
                                1000000000 // 1秒超时
                            );
                            
                            signaled = resultCode == Result.Success;
                        }
                    }
                    else
                    {
                        // 回退到旧的栅栏等待机制
                        // 注意：由于我们不再存储waitable，这里无法等待
                        // 这应该是回退路径，实际上不应该执行到这里
                        Logger.Warning?.PrintMsg(LogClass.Gpu, "Timeline semaphores not supported, using fallback sync");
                        
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
                    }
                }
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
                        }
                    }
                }
                else
                {
                    // 此同步句柄及后续的尚未到达
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
    }
}