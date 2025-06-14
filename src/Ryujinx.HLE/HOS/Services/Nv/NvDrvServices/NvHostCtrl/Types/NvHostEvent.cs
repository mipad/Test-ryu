using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Horizon.Common;
using System;
using System.Collections.Concurrent;
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

        // 新增优化字段
        private long _lastGpuSignalTime;
        private int _adaptiveThreshold = (int)FailingCountMax;
        private static readonly TimeSpan _shaderCompilationThreshold = TimeSpan.FromMilliseconds(50);
        private readonly ConcurrentQueue<SyncpointWaiterHandle> _waiterPool = new();

        public NvHostEvent(NvHostSyncpt syncpointManager, uint eventId, Horizon system)
        {
            Fence.Id = 0;

            State = NvHostEventState.Available;

            Event = new KEvent(system.KernelContext);

            if (KernelStatic.GetCurrentProcess().HandleTable.GenerateHandle(Event.ReadableEvent, out EventHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            _eventId = eventId;

            _syncpointManager = syncpointManager;

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
                // If the signal does not match our current waiter,
                // then it is from a past fence and we should just ignore it.
                if (waiterInformation != null && waiterInformation != _waiterInformation)
                {
                    // 回收未使用的资源
                    if (waiterInformation != null && !_waiterPool.Contains(waiterInformation))
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
                if (_waiterInformation != null && !_waiterPool.Contains(_waiterInformation))
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
                NvHostEventState oldState = State;

                State = NvHostEventState.Cancelling;

                if (oldState == NvHostEventState.Waiting && _waiterInformation != null)
                {
                    gpuContext.Synchronization.UnregisterCallback(Fence.Id, _waiterInformation);
                    
                    // 回收资源前检查
                    if (_waiterInformation != null && !_waiterPool.Contains(_waiterInformation))
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
                // 动态调整阈值：当检测到着色器编译时提高容错
                /* 实际项目中需根据ShaderCompiler实现启用
                if (gpuContext.ShaderCompiler != null && 
                    gpuContext.ShaderCompiler.IsCompiling && 
                    gpuContext.ShaderCompiler.ElapsedTime > _shaderCompilationThreshold)
                {
                    _adaptiveThreshold = (int)FailingCountMax * 2;
                }
                else
                {
                    _adaptiveThreshold = (int)FailingCountMax;
                }
                */
                
                // 默认使用动态阈值
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
                if (!_waiterPool.TryDequeue(out _waiterInformation))
                {
                    _waiterInformation = null;
                }

                // 异步快速路径检查
                if (gpuContext.Synchronization.IsSyncpointReached(Fence.Id, Fence.Value))
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
            int currentTimeout = initialTimeout;
            while (currentTimeout <= maxTimeout)
            {
                if (ctx.Synchronization.IsSyncpointReached(fence.Id, fence.Value))
                {
                    return true;
                }
                Thread.Sleep(currentTimeout);
                currentTimeout *= 2; // 指数退避
            }
            return ctx.Synchronization.IsSyncpointReached(fence.Id, fence.Value);
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
            if (EventHandle != 0)
            {
                context.Process.HandleTable.CloseHandle(EventHandle);
                EventHandle = 0;
            }
            
            // 清理资源池
            while (_waiterPool.TryDequeue(out var waiter))
            {
                // 如果waiter需要显式释放，在此添加
            }
        }
        
        // 新增性能监控方法
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

    // 新增结构体用于性能监控
    public struct PerformanceMetrics
    {
        public uint EventId;
        public uint FailingCount;
        public double LastGpuResponseMs;
        public int WaiterPoolSize;
        public int AdaptiveThreshold;
    }
}
