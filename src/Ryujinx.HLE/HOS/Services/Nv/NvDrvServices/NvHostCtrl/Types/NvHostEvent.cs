using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Horizon.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl
{
    class NvHostEvent
    {
        public NvFence Fence;
        public NvHostEventState State;
        public KEvent Event;
        public int EventHandle;

        private readonly uint _eventId;
#pragma warning disable IDE0052 // Remove unread private member
        private readonly NvHostSyncpt _syncpointManager;
#pragma warning restore IDE0052
        private SyncpointWaiterHandle _waiterInformation;

        private NvFence _previousFailingFence;
        private uint _failingCount;

        public readonly object Lock = new();

        /// <summary>
        /// Max failing count until waiting on CPU.
        /// FIXME: This seems enough for most of the cases, reduce if needed.
        /// </summary>
        private const uint FailingCountMax = 2;

        // 优化字段
        private long _lastGpuSignalTime;
        private int _adaptiveThreshold = (int)FailingCountMax;
        private static readonly TimeSpan _shaderCompilationThreshold = TimeSpan.FromMilliseconds(50);
        private readonly Queue<SyncpointWaiterHandle> _waiterPool = new();

        // GPU上下文引用
        private readonly GpuContext _gpuContext;

        public NvHostEvent(NvHostSyncpt syncpointManager, uint eventId, Horizon system, GpuContext gpuContext)
        {
            Fence.Id = 0;
            State = NvHostEventState.Available;
            
            // 事件0的特殊处理
            if (eventId == 0)
            {
                Logger.Warning?.Print(LogClass.ServiceNv, 
                    $"Initializing system event {eventId} - special handling required");
            }

            Event = new KEvent(system.KernelContext);

            if (KernelStatic.GetCurrentProcess().HandleTable.GenerateHandle(Event.ReadableEvent, out EventHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            _eventId = eventId;
            _syncpointManager = syncpointManager;
            _gpuContext = gpuContext;

            ResetFailingState();
        }

        private void ResetFailingState()
        {
            _previousFailingFence.Id = NvFence.InvalidSyncPointId;
            _previousFailingFence.Value = 0;
            _failingCount = 0;
        }

        private void Signal()
        {
            lock (Lock)
            {
                // 忽略无效事件的信号
                if (State == NvHostEventState.Invalid)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv, 
                        $"Ignoring signal for invalid event {_eventId}");
                    return;
                }

                NvHostEventState oldState = State;

                State = NvHostEventState.Signaling;

                if (oldState == NvHostEventState.Waiting)
                {
                    Event.WritableEvent.Signal();
                }

                State = NvHostEventState.Signaled;
            }
        }

        private void GpuSignaled(SyncpointWaiterHandle waiterInformation)
        {
            lock (Lock)
            {
                // 忽略无效事件的回调
                if (State == NvHostEventState.Invalid)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv,
                        $"Ignoring GPU signal for invalid event {_eventId}");
                    return;
                }

                // If the signal does not match our current waiter,
                // then it is from a past fence and we should just ignore it.
                if (waiterInformation != null && waiterInformation != _waiterInformation)
                {
                    // 回收未使用的资源
                    if (waiterInformation != null)
                    {
                        _waiterPool.Enqueue(waiterInformation);
                    }
                    return;
                }

                // 记录GPU响应时间用于监控
                _lastGpuSignalTime = Stopwatch.GetTimestamp();
                
                ResetFailingState();
                Signal();
                
                // 回收当前资源
                if (_waiterInformation != null)
                {
                    _waiterPool.Enqueue(_waiterInformation);
                    _waiterInformation = null;
                }
            }
        }

        public void Cancel(GpuContext gpuContext)
        {
            lock (Lock)
            {
                // 忽略无效事件的取消
                if (State == NvHostEventState.Invalid)
                {
                    Logger.Debug?.Print(LogClass.ServiceNv,
                        $"Ignoring cancel for invalid event {_eventId}");
                    return;
                }

                NvHostEventState oldState = State;

                State = NvHostEventState.Cancelling;

                if (oldState == NvHostEventState.Waiting && _waiterInformation != null)
                {
                    gpuContext.Synchronization.UnregisterCallback(Fence.Id, _waiterInformation);
                    
                    // 回收资源前检查
                    if (_waiterInformation != null)
                    {
                        _waiterPool.Enqueue(_waiterInformation);
                    }
                    _waiterInformation = null;

                    if (_previousFailingFence.Id == Fence.Id && _previousFailingFence.Value == Fence.Value)
                    {
                        _failingCount++;
                    }
                    else
                    {
                        _failingCount = 1;

                        _previousFailingFence = Fence;
                    }
                }

                State = NvHostEventState.Cancelled;

                Event.WritableEvent.Clear();

                // 智能退避策略
                var backoffTime = CalculateBackoffTime();
                if (backoffTime > 0)
                {
                    Logger.Info?.Print(LogClass.ServiceNv, 
                        $"Smart backoff: {backoffTime}ms for event {_eventId}");
                    Thread.Sleep(backoffTime);
                }
            }
        }

        public bool Wait(GpuContext gpuContext, NvFence fence)
        {
            lock (Lock)
            {
                // 拒绝在无效事件上等待
                if (State == NvHostEventState.Invalid)
                {
                    Logger.Error?.Print(LogClass.ServiceNv, 
                        $"Rejecting wait on invalid event {_eventId}");
                    return false;
                }

                // 使用动态阈值
                _adaptiveThreshold = (int)FailingCountMax;

                if (_failingCount >= _adaptiveThreshold)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, 
                        $"GPU processing slow (fails: {_failingCount}). Waiting on CPU...");

                    // 使用增量等待策略
                    bool waitResult = IncrementalWait(gpuContext, fence, 16, 100);
                    
                    if (waitResult)
                    {
                        ResetFailingState();
                        return false;
                    }
                }

                Fence = fence;
                State = NvHostEventState.Waiting;

                // 尝试从资源池获取或创建新的waiter
                if (_waiterPool.Count > 0)
                {
                    _waiterInformation = _waiterPool.Dequeue();
                }
                else
                {
                    _waiterInformation = null;
                }

                // 同步点快速检查
                uint currentValue = gpuContext.Synchronization.GetSyncpointValue(Fence.Id);
                if (currentValue >= Fence.Value)
                {
                    if (_waiterInformation != null)
                    {
                        _waiterPool.Enqueue(_waiterInformation);
                    }
                    Signal();
                    return false;
                }

                // 注册回调
                _waiterInformation = gpuContext.Synchronization.RegisterCallbackOnSyncpoint(
                    Fence.Id, 
                    Fence.Value, 
                    GpuSignaled);

                return true;
            }
        }
        
        // 增量等待方法实现
        private bool IncrementalWait(GpuContext ctx, NvFence fence, int initialTimeout, int maxTimeout)
        {
            // 事件0的特殊超时处理
            int event0Multiplier = (_eventId == 0) ? 2 : 1;
            int adjustedMaxTimeout = maxTimeout * event0Multiplier;
            
            int currentTimeout = initialTimeout;
            while (currentTimeout <= adjustedMaxTimeout)
            {
                uint currentValue = ctx.Synchronization.GetSyncpointValue(fence.Id);
                if (currentValue >= fence.Value)
                {
                    return true;
                }
                Thread.Sleep(currentTimeout);
                currentTimeout *= 2; // 指数退避
            }
            
            // 最终检查
            uint finalValue = ctx.Synchronization.GetSyncpointValue(fence.Id);
            return finalValue >= fence.Value;
        }

        // 智能退避计算
        private int CalculateBackoffTime()
        {
            // 指数退避算法，最大 100ms
            return _failingCount > 0 ? 
                Math.Min(100, (int)Math.Pow(2, _failingCount)) : 0;
        }

        public string DumpState(GpuContext gpuContext)
        {
            string res = $"\nNvHostEvent {_eventId}:\n";
            res += $"\tState: {State}\n";
            res += $"\tFailing Count: {_failingCount}/{_adaptiveThreshold}\n";
            res += $"\tWaiter Pool Size: {_waiterPool.Count}\n";

            if (State == NvHostEventState.Waiting)
            {
                res += "\tFence:\n";
                res += $"\t\tId            : {Fence.Id}\n";
                res += $"\t\tThreshold     : {Fence.Value}\n";
                res += $"\t\tCurrent Value : {gpuContext.Synchronization.GetSyncpointValue(Fence.Id)}\n";
                res += $"\t\tWaiter Valid  : {_waiterInformation != null}\n";
            }

            return res;
        }

        public void CloseEvent(ServiceCtx context)
        {
            lock (Lock)
            {
                // 标记事件为失效状态
                State = NvHostEventState.Invalid;
                Logger.Info?.Print(LogClass.ServiceNv, $"Marking event {_eventId} as invalid");

                // 取消所有注册的回调
                if (_waiterInformation != null)
                {
                    _gpuContext.Synchronization.UnregisterCallback(Fence.Id, _waiterInformation);
                    _waiterInformation = null;
                }

                // 清理资源池
                while (_waiterPool.Count > 0)
                {
                    var waiter = _waiterPool.Dequeue();
                    _gpuContext.Synchronization.UnregisterCallback(Fence.Id, waiter);
                }

                if (EventHandle != 0)
                {
                    context.Process.HandleTable.CloseHandle(EventHandle);
                    EventHandle = 0;
                }
            }
        }
        
        // 性能监控方法
        public PerformanceMetrics GetMetrics()
        {
            double lastResponseMs = -1;
            if (_lastGpuSignalTime != 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastGpuSignalTime);
                lastResponseMs = elapsed.TotalMilliseconds;
            }
            
            return new PerformanceMetrics {
                EventId = _eventId,
                FailingCount = _failingCount,
                LastGpuResponseMs = lastResponseMs,
                WaiterPoolSize = _waiterPool.Count,
                AdaptiveThreshold = _adaptiveThreshold
            };
        }
    }

    // 结构体用于性能监控
    public struct PerformanceMetrics
    {
        public uint EventId;
        public uint FailingCount;
        public double LastGpuResponseMs;
        public int WaiterPoolSize;
        public int AdaptiveThreshold;
    }
}
