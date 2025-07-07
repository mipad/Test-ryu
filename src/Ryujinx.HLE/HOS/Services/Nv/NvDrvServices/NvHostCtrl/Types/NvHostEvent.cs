using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Horizon.Common;
using System;
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
        private readonly NvHostSyncpt _syncpointManager;
        private SyncpointWaiterHandle _waiterInformation;

        private NvFence _previousFailingFence;
        private uint _failingCount;

        public readonly object Lock = new();

        private const uint FailingCountMax = 2;
        private long _lastGpuSignalTime;
        private int _adaptiveThreshold = (int)FailingCountMax;
        private static readonly TimeSpan _shaderCompilationThreshold = TimeSpan.FromMilliseconds(50);
        private readonly Queue<SyncpointWaiterHandle> _waiterPool = new();

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
                if (State == NvHostEventState.Waiting || State == NvHostEventState.Signaling)
                {
                    State = NvHostEventState.Signaled;
                    Event.WritableEvent.Signal();
                    Logger.Debug?.Print(LogClass.ServiceNv, $"Event {_eventId} signaled (State={State})");
                }
            }
        }

        private void GpuSignaled(SyncpointWaiterHandle waiterInformation)
        {
            lock (Lock)
            {
                if (waiterInformation != null && waiterInformation != _waiterInformation)
                {
                    _waiterPool.Enqueue(waiterInformation);
                    return;
                }

                _lastGpuSignalTime = Stopwatch.GetTimestamp();
                ResetFailingState();
                Signal();

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
                NvHostEventState oldState = State;
                State = NvHostEventState.Cancelling;

                if (oldState == NvHostEventState.Waiting && _waiterInformation != null)
                {
                    gpuContext.Synchronization.UnregisterCallback(Fence.Id, _waiterInformation);
                    _waiterPool.Enqueue(_waiterInformation);
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

                int backoffTime = CalculateBackoffTime();
                if (backoffTime > 0)
                {
                    Logger.Info?.Print(LogClass.ServiceNv, $"Smart backoff: {backoffTime}ms for event {_eventId}");
                    Thread.Sleep(backoffTime);
                }
            }
        }

        public bool Wait(GpuContext gpuContext, NvFence fence)
        {
            lock (Lock)
            {
                _adaptiveThreshold = (int)FailingCountMax;

                if (_failingCount >= _adaptiveThreshold)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"GPU processing slow (fails: {_failingCount}). Waiting on CPU...");
                    if (IncrementalWait(gpuContext, fence, 16, 100))
                    {
                        ResetFailingState();
                        return false;
                    }
                }

                Fence = fence;
                State = NvHostEventState.Waiting;

                _waiterInformation = _waiterPool.Count > 0 ? _waiterPool.Dequeue() : null;

                // Double-check syncpoint value to avoid race condition
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

                _waiterInformation = gpuContext.Synchronization.RegisterCallbackOnSyncpoint(
                    Fence.Id, 
                    Fence.Value, 
                    GpuSignaled);

                Logger.Debug?.Print(LogClass.ServiceNv, 
                    $"Event {_eventId} waiting (Current: {currentValue}, Threshold: {Fence.Value})");
                return true;
            }
        }

        private bool IncrementalWait(GpuContext ctx, NvFence fence, int initialTimeout, int maxTimeout)
        {
            int currentTimeout = initialTimeout;
            while (currentTimeout <= maxTimeout)
            {
                uint currentValue = ctx.Synchronization.GetSyncpointValue(fence.Id);
                if (currentValue >= fence.Value)
                {
                    return true;
                }
                Thread.Sleep(currentTimeout);
                currentTimeout *= 2;
            }
            return ctx.Synchronization.GetSyncpointValue(fence.Id) >= fence.Value;
        }

        private int CalculateBackoffTime()
        {
            return _failingCount > 0 ? Math.Min(100, (int)Math.Pow(2, _failingCount)) : 0;
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
            while (_waiterPool.Count > 0)
            {
                _waiterPool.Dequeue();
            }
        }

        public PerformanceMetrics GetMetrics()
        {
            double lastResponseMs = -1;
            if (_lastGpuSignalTime != 0)
            {
                lastResponseMs = Stopwatch.GetElapsedTime(_lastGpuSignalTime).TotalMilliseconds;
            }
            return new PerformanceMetrics
            {
                EventId = _eventId,
                FailingCount = _failingCount,
                LastGpuResponseMs = lastResponseMs,
                WaiterPoolSize = _waiterPool.Count,
                AdaptiveThreshold = _adaptiveThreshold
            };
        }
    }

    public struct PerformanceMetrics
    {
        public uint EventId;
        public uint FailingCount;
        public double LastGpuResponseMs;
        public int WaiterPoolSize;
        public int AdaptiveThreshold;
    }
}
