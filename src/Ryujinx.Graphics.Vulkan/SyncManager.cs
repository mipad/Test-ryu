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
        
        // 批量创建缓冲区
        private readonly List<SyncHandle> _batchCreateBuffer = new();
        private readonly object _batchCreateLock = new();
        private const int MaxBatchSize = 16;

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

        // 批量创建同步对象
        public void CreateBulk(ulong[] ids, bool[] strictFlags)
        {
            if (ids == null || strictFlags == null || ids.Length != strictFlags.Length)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, 
                    $"批量创建同步对象失败: 参数无效");
                return;
            }
            
            if (ids.Length == 0)
                return;
                
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量创建同步对象: 数量={ids.Length}");
            
            // 批量分配时间线值
            ulong[] timelineValues = new ulong[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                timelineValues[i] = _nextTimelineValue++;
            }
            
            // 检查是否需要立即刷新（严格模式）
            bool anyStrict = false;
            for (int i = 0; i < strictFlags.Length; i++)
            {
                if (strictFlags[i])
                {
                    anyStrict = true;
                    break;
                }
            }
            
            if (anyStrict)
            {
                // 严格模式：立即刷新并批量提交
                _gd.FlushAllCommands();
                
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    // 批量提交时间线信号量
                    _gd.CommandBufferPool.AddTimelineSignals(_gd.TimelineSemaphore, timelineValues);
                    
                    // 创建专门的命令缓冲区来批量发送信号
                    var cbs = _gd.CommandBufferPool.Rent();
                    try
                    {
                        _gd.EndAndSubmitCommandBuffer(cbs, 0);
                    }
                    finally
                    {
                        // 注意：EndAndSubmitCommandBuffer已经处理了返回
                    }
                }
            }
            
            // 批量创建同步句柄
            List<SyncHandle> handles = new List<SyncHandle>();
            for (int i = 0; i < ids.Length; i++)
            {
                SyncHandle handle = new()
                {
                    ID = ids[i],
                    TimelineValue = timelineValues[i],
                    FlushId = _flushId,
                };
                
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
                {
                    // 创建共享的TimelineFenceHolder
                    handle.TimelineFenceHolder = new TimelineFenceHolder(_gd, _device, _gd.TimelineSemaphore);
                    
                    // 批量添加到命令缓冲区
                    if (!strictFlags[i]) // 非严格模式才需要延迟添加
                    {
                        int currentCbIndex = _gd.GetCurrentCommandBufferIndex();
                        if (currentCbIndex >= 0)
                        {
                            _gd.CommandBufferPool.AddTimelineSignalToBuffer(currentCbIndex, _gd.TimelineSemaphore, timelineValues[i]);
                            _gd.CommandBufferPool.AddTimelineFenceHolder(currentCbIndex, handle.TimelineFenceHolder);
                        }
                        else
                        {
                            // 添加到待处理队列
                            _gd.CommandBufferPool.AddPendingTimelineSignals(-1, new ulong[] { timelineValues[i] });
                        }
                    }
                }
                else
                {
                    // 回退到传统的MultiFenceHolder
                    handle.MultiFenceHolder = new MultiFenceHolder();
                    if (strictFlags[i] || _gd.InterruptAction == null)
                    {
                        _gd.CommandBufferPool.AddWaitable(handle.MultiFenceHolder);
                    }
                    else
                    {
                        _gd.CommandBufferPool.AddInUseWaitable(handle.MultiFenceHolder);
                    }
                }
                
                handles.Add(handle);
            }
            
            // 添加到主列表
            lock (_handles)
            {
                _handles.AddRange(handles);
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"批量创建同步对象完成: 数量={handles.Count}");
        }

        // 单个创建（兼容性）
        public void Create(ulong id, bool strict)
        {
            CreateBulk(new ulong[] { id }, new bool[] { strict });
        }

        // 批量获取当前同步状态
        public ulong[] GetCurrentBulk(ulong[] ids)
        {
            if (ids == null || ids.Length == 0)
                return Array.Empty<ulong>();
                
            ulong[] results = new ulong[ids.Length];
            
            lock (_handles)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    ulong id = ids[i];
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
                            
                            if (handle.ID == id)
                            {
                                // 检查时间线信号量是否已达到此值
                                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && handle.TimelineFenceHolder != null)
                                {
                                    if (handle.TimelineFenceHolder.WaitForSignals(_gd.Api, _device, 0))
                                    {
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
                                break;
                            }
                        }
                    }
                    
                    results[i] = lastHandle;
                }
            }
            
            return results;
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

        // 批量等待多个同步对象
        public void WaitBulk(ulong[] ids)
        {
            if (ids == null || ids.Length == 0)
                return;
                
            Logger.Info?.PrintMsg(LogClass.Gpu, 
                $"批量等待同步对象: 数量={ids.Length}");
            
            // 收集所有需要等待的同步句柄
            List<SyncHandle> handlesToWait = new List<SyncHandle>();
            
            lock (_handles)
            {
                foreach (ulong id in ids)
                {
                    if ((long)(_firstHandle - id) > 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"同步对象 ID={id} 已发出信号或已删除，无需等待");
                        continue;
                    }

                    foreach (SyncHandle handle in _handles)
                    {
                        if (handle.ID == id)
                        {
                            handlesToWait.Add(handle);
                            break;
                        }
                    }
                }
            }
            
            if (handlesToWait.Count == 0)
                return;
                
            // 按时间线值分组等待
            Dictionary<TimelineFenceHolder, List<ulong>> timelineGroups = new();
            List<SyncHandle> multiFenceHandles = new();
            
            foreach (var handle in handlesToWait)
            {
                if (handle.Signalled)
                    continue;
                    
                if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && handle.TimelineFenceHolder != null)
                {
                    if (!timelineGroups.ContainsKey(handle.TimelineFenceHolder))
                    {
                        timelineGroups[handle.TimelineFenceHolder] = new List<ulong>();
                    }
                    timelineGroups[handle.TimelineFenceHolder].Add(handle.TimelineValue);
                }
                else if (handle.MultiFenceHolder != null)
                {
                    multiFenceHandles.Add(handle);
                }
            }
            
            long beforeTicks = Stopwatch.GetTimestamp();
            
            // 批量等待时间线信号量
            foreach (var kvp in timelineGroups)
            {
                var holder = kvp.Key;
                var values = kvp.Value;
                
                // 检查是否需要刷新
                bool needsFlush = false;
                foreach (var value in values)
                {
                    // 找到对应的句柄
                    foreach (var handle in handlesToWait)
                    {
                        if (handle.TimelineValue == value && handle.NeedsFlush(_flushId))
                        {
                            needsFlush = true;
                            break;
                        }
                    }
                    if (needsFlush) break;
                }
                
                if (needsFlush)
                {
                    _gd.InterruptAction(() =>
                    {
                        _gd.FlushAllCommands();
                    });
                }
                
                // 批量等待多个值
                bool signaled = holder.WaitForMultipleSignals(_gd.Api, _device, values.ToArray(), 1000000000);
                
                if (!signaled)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, 
                        $"批量等待时间线信号量失败，包含 {values.Count} 个值");
                }
                else
                {
                    // 标记所有已发出信号的句柄
                    foreach (var value in values)
                    {
                        foreach (var handle in handlesToWait)
                        {
                            if (handle.TimelineValue == value)
                            {
                                handle.Signalled = true;
                                break;
                            }
                        }
                    }
                }
            }
            
            // 等待传统的MultiFenceHolder
            foreach (var handle in multiFenceHandles)
            {
                if (handle.Signalled)
                    continue;
                    
                if (handle.NeedsFlush(_flushId))
                {
                    _gd.InterruptAction(() =>
                    {
                        _gd.FlushAllCommands();
                    });
                }
                
                bool signaled = handle.MultiFenceHolder.WaitForFences(_gd.Api, _device, 1000000000);
                
                if (!signaled)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, 
                        $"VK Sync Object {handle.ID} failed to signal within 1000ms. Continuing...");
                }
                else
                {
                    handle.Signalled = true;
                }
            }
            
            _waitTicks += Stopwatch.GetTimestamp() - beforeTicks;
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"批量等待完成: 处理数量={handlesToWait.Count}，耗时={Stopwatch.GetTimestamp() - beforeTicks} ticks");
        }

        // 单个等待（兼容性）
        public void Wait(ulong id)
        {
            WaitBulk(new ulong[] { id });
        }

        public void Cleanup()
        {
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"开始清理同步对象，当前句柄数量={_handles.Count}");
            
            // 批量清理：收集所有可以清理的句柄
            List<SyncHandle> toRemove = new List<SyncHandle>();
            
            lock (_handles)
            {
                foreach (var handle in _handles)
                {
                    if (handle.NeedsFlush(_flushId))
                    {
                        continue; // 还需要刷新，不能清理
                    }
                    
                    bool signaled = false;
                    
                    if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0 && handle.TimelineFenceHolder != null)
                    {
                        // 检查时间线信号量是否已达到此值
                        signaled = handle.TimelineFenceHolder.WaitForSignals(_gd.Api, _device, 0);
                        
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"检查同步对象 ID={handle.ID}: 已发出信号={signaled}");
                    }
                    else if (handle.MultiFenceHolder != null)
                    {
                        // 使用传统的MultiFenceHolder检查
                        signaled = handle.MultiFenceHolder.WaitForFences(_gd.Api, _device, 0);
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"检查同步对象 ID={handle.ID} (传统): 已发出信号={signaled}");
                    }
                    else
                    {
                        // 回退：我们无法检查，假设已发出信号
                        signaled = true;
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"时间线信号量不支持，假设同步对象 ID={handle.ID} 已发出信号");
                    }

                    if (signaled)
                    {
                        toRemove.Add(handle);
                    }
                }
                
                // 批量移除
                foreach (var handle in toRemove)
                {
                    lock (handle)
                    {
                        if (handle.ID >= _firstHandle)
                        {
                            _firstHandle = handle.ID + 1;
                        }
                        
                        // 清理TimelineFenceHolder资源
                        handle.TimelineFenceHolder?.Clear();
                        
                        // 清理MultiFenceHolder资源
                        if (handle.MultiFenceHolder != null)
                        {
                            Array.Clear(handle.MultiFenceHolder.Fences);
                            MultiFenceHolder.FencePool.Release(handle.MultiFenceHolder.Fences);
                            handle.MultiFenceHolder = null;
                        }
                        
                        _handles.Remove(handle);
                        Logger.Debug?.PrintMsg(LogClass.Gpu, 
                            $"删除同步对象 ID={handle.ID}");
                    }
                }
            }
            
            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"清理完成: 删除数量={toRemove.Count}，剩余句柄数量={_handles.Count}");
        }

        public long GetAndResetWaitTicks()
        {
            long result = _waitTicks;
            _waitTicks = 0;

            Logger.Debug?.PrintMsg(LogClass.Gpu, 
                $"获取并重置等待ticks: {result}");
            
            return result;
        }
        
        // 刷新所有待处理的批量信号
        public void FlushPendingBatches()
        {
            if (_gd.SupportsTimelineSemaphores && _gd.TimelineSemaphore.Handle != 0)
            {
                // 查找所有TimelineFenceHolder并刷新
                List<TimelineFenceHolder> holders = new List<TimelineFenceHolder>();
                
                lock (_handles)
                {
                    foreach (var handle in _handles)
                    {
                        if (handle.TimelineFenceHolder != null && !holders.Contains(handle.TimelineFenceHolder))
                        {
                            holders.Add(handle.TimelineFenceHolder);
                        }
                    }
                }
                
                foreach (var holder in holders)
                {
                    holder.FlushPendingBatches(0, 1); // 强制刷新
                }
                
                Logger.Debug?.PrintMsg(LogClass.Gpu, 
                    $"刷新待处理的批量信号: 数量={holders.Count}");
            }
        }
    }
}