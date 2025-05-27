using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Horizon.Common;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl.Types
{
    internal class NvHostEvent
    {
        public NvFence Fence { get; private set; }
        public NvHostEventState State { get; private set; }
        public KEvent Event { get; }
        public int EventHandle { get; private set; }

        public ManualResetEventSlim SignalEvent { get; } = new ManualResetEventSlim(false);
        private readonly Stopwatch _stateTimer = new Stopwatch();

        private readonly uint _eventId;
#pragma warning disable IDE0052
        private readonly NvHostSyncpt _syncpointManager;
#pragma warning restore IDE0052
        private SyncpointWaiterHandle _waiterInformation;

        private NvFence _previousFailingFence;
        private uint _failingCount;

        public  object Lock { get; } = new();

        private const uint FailingCountMax = 2;

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

            // 句柄生成逻辑
            if (KernelStatic.GetCurrentProcess().HandleTable.GenerateHandle(Event.ReadableEvent, out int eventHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }
            EventHandle = eventHandle;

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
                if (waiterInformation != null && waiterInformation != _waiterInformation)
                {
                    return;
                }

                ResetFailingState();
                Signal();
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
            }
        }

        public bool Wait(GpuContext gpuContext, NvFence fence, int timeout)
        {
            lock (Lock)
            {
                if (_failingCount == FailingCountMax)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, "GPU processing thread is too slow, waiting on CPU...");
                    Fence.Wait(gpuContext, Timeout.InfiniteTimeSpan);
                    ResetFailingState();
                    return false;
                }
                else
                {
                    Fence = fence;
                    State = NvHostEventState.Waiting;
                    _waiterInformation = gpuContext.Synchronization.RegisterCallbackOnSyncpoint(Fence.Id, Fence.Value, GpuSignaled);
                    return SignalEvent.Wait(timeout);
                }
            }
        }

        public void CheckStateTimeout()
        {
            if (_stateTimer.ElapsedMilliseconds > 1000 &&
                (State == NvHostEventState.Waiting || State == NvHostEventState.Signaling))
            {
                Logger.Warning?.Print(LogClass.ServiceNv, "Event state timeout detected");
                SignalEvent.Set();
            }
        }

        public string DumpState(GpuContext gpuContext)
        {
            string res = $"\nNvHostEvent {_eventId}:\n";
            res += $"\tState: {State}\n";

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
        }
    }
}
