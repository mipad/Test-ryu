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
            public ulong TimelineValue; // 时间线信号量值（如果使用时间线信号量）
            public MultiFenceHolder Waitable; // 栅栏（如果使用栅栏）
            public ulong FlushId;
            public bool Signalled;
            public bool UsingTimeline; // 标记是否使用时间线信号量

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
        
        // 时间线信号量（可选）
        private Semaphore _timelineSemaphore;
        private bool _timelineSemaphoreValid = false;

        public SyncManager(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _handles = [];
            
            // 尝试初始化时间线信号量，如果失败则使用栅栏
            InitializeTimelineSemaphore();
        }

        private void InitializeTimelineSemaphore()
        {
            if (!_gd.SupportsTimelineSemaphores || _gd.TimelineSemaphoreApi == null)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, "Timeline semaphores not supported, using fence-based synchronization");
                return;
            }

            try
            {
                unsafe
                {
                    var semaphoreTypeCreateInfo = new SemaphoreTypeCreateInfoKHR
                    {
                        SType = StructureType.SemaphoreTypeCreateInfo,
                        SemaphoreType = SemaphoreTypeKHR.Timeline,
                        InitialValue = 0
                    };

                    var semaphoreCreateInfo = new SemaphoreCreateInfo
                    {
                        SType = StructureType.SemaphoreCreateInfo,
                        PNext = &semaphoreTypeCreateInfo
                    };

                    var result = _gd.Api.CreateSemaphore(_device, semaphoreCreateInfo, null, out _timelineSemaphore);
                    
                    if (result == Result.Success)
                    {
                        _timelineSemaphoreValid = true;
                        Logger.Info?.PrintMsg(LogClass.Gpu, "Timeline semaphore initialized successfully");
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, $"Failed to create timeline semaphore: {result}, falling back to fences");
                        _timelineSemaphore = default;
                        _timelineSemaphoreValid = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Exception creating timeline semaphore: {ex.Message}, falling back to fences");
                _timelineSemaphore = default;
                _timelineSemaphoreValid = false;
            }
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
                UsingTimeline = _timelineSemaphoreValid
            };

            if (strict || _gd.InterruptAction == null)
            {
                _gd.FlushAllCommands();
                
                if (handle.UsingTimeline)
                {
                    // 使用时间线信号量
                    handle.TimelineValue = _nextTimelineValue++;
                    
                    // 尝试使用时间线信号量提交
                    try
                    {
                        _gd.CommandBufferPool.AddTimelineSignal(_timelineSemaphore, handle.TimelineValue);
                    }
                    catch (Exception ex)
                    {
                        // 如果失败，回退到栅栏
                        Logger.Warning?.PrintMsg(LogClass.Gpu, $"Timeline semaphore signal failed: {ex.Message}, falling back to fence");
                        handle.UsingTimeline = false;
                        handle.Waitable = new MultiFenceHolder();
                        _gd.CommandBufferPool.AddWaitable(handle.Waitable);
                    }
                }
                else
                {
                    // 使用栅栏
                    handle.Waitable = new MultiFenceHolder();
                    _gd.CommandBufferPool.AddWaitable(handle.Waitable);
                }
            }
            else
            {
                // 不刷新命令，等待当前命令缓冲区完成
                if (handle.UsingTimeline)
                {
                    handle.TimelineValue = _nextTimelineValue++;
                    
                    try
                    {
                        _gd.CommandBufferPool.AddInUseTimelineSignal(_timelineSemaphore, handle.TimelineValue);
                    }
                    catch (Exception ex)
                    {
                        // 如果失败，回退到栅栏
                        Logger.Warning?.PrintMsg(LogClass.Gpu, $"In-use timeline semaphore signal failed: {ex.Message}, falling back to fence");
                        handle.UsingTimeline = false;
                        handle.Waitable = new MultiFenceHolder();
                        _gd.CommandBufferPool.AddInUseWaitable(handle.Waitable);
                    }
                }
                else
                {
                    handle.Waitable = new MultiFenceHolder();
                    _gd.CommandBufferPool.AddInUseWaitable(handle.Waitable);
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

                        bool signaled = false;
                        
                        if (handle.UsingTimeline && _timelineSemaphoreValid)
                        {
                            try
                            {
                                unsafe
                                {
                                    ulong currentValue;
                                    var result = _gd.TimelineSemaphoreApi.GetSemaphoreCounterValue(
                                        _device, _timelineSemaphore, &currentValue);
                                    
                                    if (result == Result.Success && currentValue >= handle.TimelineValue)
                                    {
                                        signaled = true;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // 查询失败，回退到栅栏检查
                                Logger.Warning?.PrintMsg(LogClass.Gpu, $"Timeline semaphore query failed: {ex.Message}, checking fence instead");
                                handle.UsingTimeline = false;
                            }
                        }
                        
                        // 如果不使用时间线信号量或查询失败，检查栅栏
                        if (!signaled && !handle.UsingTimeline && handle.Waitable != null)
                        {
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
                    
                    if (result.UsingTimeline && _timelineSemaphoreValid)
                    {
                        try
                        {
                            unsafe
                            {
                                var waitInfo = new SemaphoreWaitInfoKHR
                                {
                                    SType = StructureType.SemaphoreWaitInfo,
                                    SemaphoreCount = 1,
                                    PSemaphores = &_timelineSemaphore,
                                    PValues = &result.TimelineValue
                                };

                                var resultCode = _gd.TimelineSemaphoreApi.WaitSemaphores(
                                    _device, 
                                    &waitInfo, 
                                    1000000000 // 1秒超时
                                );
                                
                                signaled = resultCode == Result.Success;
                                
                                if (!signaled && resultCode != Result.Timeout)
                                {
                                    Logger.Warning?.PrintMsg(LogClass.Gpu, $"Timeline semaphore wait failed: {resultCode}, falling back to fence wait");
                                    result.UsingTimeline = false;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.PrintMsg(LogClass.Gpu, $"Exception waiting on timeline semaphore: {ex.Message}, falling back to fence");
                            result.UsingTimeline = false;
                        }
                    }
                    
                    // 如果不使用时间线信号量或等待失败，使用栅栏
                    if (!signaled && !result.UsingTimeline && result.Waitable != null)
                    {
                        signaled = result.Waitable.WaitForFences(_gd.Api, _device, 1000000000);
                        
                        if (!signaled)
                        {
                            Logger.Error?.PrintMsg(LogClass.Gpu, $"VK Sync Object {result.ID} failed to signal within 1000ms. Continuing...");
                        }
                    }

                    if (signaled)
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
                
                if (first.UsingTimeline && _timelineSemaphoreValid)
                {
                    try
                    {
                        unsafe
                        {
                            ulong currentValue;
                            var result = _gd.TimelineSemaphoreApi.GetSemaphoreCounterValue(
                                _device, _timelineSemaphore, &currentValue);
                            
                            if (result == Result.Success)
                            {
                                signaled = currentValue >= first.TimelineValue;
                            }
                            else
                            {
                                // 查询失败，回退到栅栏检查
                                first.UsingTimeline = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.PrintMsg(LogClass.Gpu, $"Timeline semaphore query in cleanup failed: {ex.Message}, checking fence");
                        first.UsingTimeline = false;
                    }
                }
                
                // 如果不使用时间线信号量或查询失败，检查栅栏
                if (!signaled && !first.UsingTimeline && first.Waitable != null)
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
                            
                            if (!first.UsingTimeline && first.Waitable != null)
                            {
                                Array.Clear(first.Waitable.Fences);
                                MultiFenceHolder.FencePool.Release(first.Waitable.Fences);
                                first.Waitable = null;
                            }
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
        
        public void Dispose()
        {
            if (_timelineSemaphoreValid && _timelineSemaphore.Handle != 0)
            {
                _gd.Api.DestroySemaphore(_device, _timelineSemaphore, null);
                _timelineSemaphore = default;
                _timelineSemaphoreValid = false;
            }
        }
    }
}
