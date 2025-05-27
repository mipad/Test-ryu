using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl.Types;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.HLE.HOS.Services.Settings;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl
{
    internal class NvHostCtrlDeviceFile : NvDeviceFile
    {
        public const int EventsCount = 64;

        private readonly bool _isProductionMode;
        private readonly Switch _device;
        private readonly NvHostEvent[] _events;
        private readonly object[] _eventLocks;
        private readonly ConcurrentQueue<uint> _freeEventIndices = new ConcurrentQueue<uint>();

        public NvHostCtrlDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, owner)
        {
            if (NxSettings.Settings.TryGetValue("nv!rmos_set_production_mode", out object productionModeSetting))
            {
                _isProductionMode = ((string)productionModeSetting) != "0";
            }
            else
            {
                _isProductionMode = true;
            }

            _device = context.Device;

            _events = new NvHostEvent[EventsCount];
            _eventLocks = new object[EventsCount];
            
            for (int i = 0; i < EventsCount; i++)
            {
                _eventLocks[i] = new object();
                _freeEventIndices.Enqueue((uint)i);
            }
        }

        public override NvInternalResult Ioctl(NvIoctl command, Span<byte> arguments)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvHostCustomMagic)
            {
                switch (command.Number)
                {
                    case 0x14:
                        result = CallIoctlMethod<NvFence>(SyncptRead, arguments);
                        break;
                    case 0x15:
                        result = CallIoctlMethod<uint>(SyncptIncr, arguments);
                        break;
                    case 0x16:
                        byte[] argCopy = arguments.ToArray();
                        Task.Run(() => HandleSyncptWaitAsync(argCopy));
                        result = NvInternalResult.TryAgain;
                        break;
                    case 0x19:
                        result = CallIoctlMethod<SyncptWaitExArguments>(SyncptWaitEx, arguments);
                        break;
                    case 0x1a:
                        result = CallIoctlMethod<NvFence>(SyncptReadMax, arguments);
                        break;
                    case 0x1b:
                        GetConfigurationArguments configArgument = GetConfigurationArguments.FromSpan(arguments);
                        result = GetConfig(configArgument);
                        if (result == NvInternalResult.Success)
                        {
                            configArgument.CopyTo(arguments);
                        }
                        break;
                    case 0x1c:
                        result = CallIoctlMethod<uint>(EventSignal, arguments);
                        break;
                    case 0x1d:
                        result = CallIoctlMethod<EventWaitArguments>(EventWait, arguments);
                        break;
                    case 0x1e:
                        result = CallIoctlMethod<EventWaitArguments>(EventWaitAsync, arguments);
                        break;
                    case 0x1f:
                        result = CallIoctlMethod<uint>(EventRegister, arguments);
                        break;
                    case 0x20:
                        result = CallIoctlMethod<uint>(EventUnregister, arguments);
                        break;
                    case 0x21:
                        result = CallIoctlMethod<ulong>(EventKill, arguments);
                        break;
                }
            }

            return result;
        }

        private async Task HandleSyncptWaitAsync(byte[] argumentData)
        {
            SyncptWaitArguments waitArgs = SyncptWaitArguments.FromSpan(argumentData);
            NvInternalResult res = SyncptWait(ref waitArgs);
            if (res == NvInternalResult.Success)
            {
                using (var writer = new BinaryWriter(new MemoryStream(arguments.ToArray())))
                {
                    waitArgs.Write(writer);
                }
            }
        }

        private int QueryEvent(uint eventId)
        {
            uint eventSlot;
            uint syncpointId;

            if ((eventId >> 28) == 1)
            {
                eventSlot = eventId & 0xFFFF;
                syncpointId = (eventId >> 16) & 0xFFF;
            }
            else
            {
                eventSlot = eventId & 0xFF;
                syncpointId = eventId >> 4;
            }

            if (eventSlot >= EventsCount)
                return 0;

            lock (_eventLocks[eventSlot])
            {
                if (_events[eventSlot] == null || _events[eventSlot].Fence.Id != syncpointId)
                {
                    return 0;
                }
                return _events[eventSlot].EventHandle;
            }
        }

        public override NvInternalResult QueryEvent(out int eventHandle, uint eventId)
        {
            eventHandle = QueryEvent(eventId);
            return eventHandle != 0 ? NvInternalResult.Success : NvInternalResult.InvalidInput;
        }

        private NvInternalResult SyncptRead(ref NvFence arguments)
        {
            return SyncptReadMinOrMax(ref arguments, max: false);
        }

        private NvInternalResult SyncptIncr(ref uint id)
        {
            if (id >= SynchronizationManager.MaxHardwareSyncpoints)
            {
                return NvInternalResult.InvalidInput;
            }

            _device.System.HostSyncpoint.Increment(id);
            return NvInternalResult.Success;
        }

        private NvInternalResult SyncptWait(ref SyncptWaitArguments arguments)
        {
            uint dummyValue = 0;
            return EventWait(ref arguments.Fence, ref dummyValue, arguments.Timeout, isWaitEventAsyncCmd: false, isWaitEventCmd: false);
        }

        private NvInternalResult SyncptWaitEx(ref SyncptWaitExArguments arguments)
        {
            return EventWait(ref arguments.Input.Fence, ref arguments.Value, arguments.Input.Timeout, isWaitEventAsyncCmd: false, isWaitEventCmd: false);
        }

        private NvInternalResult SyncptReadMax(ref NvFence arguments)
        {
            return SyncptReadMinOrMax(ref arguments, max: true);
        }

        private NvInternalResult GetConfig(GetConfigurationArguments arguments)
        {
            if (!_isProductionMode && NxSettings.Settings.TryGetValue($"{arguments.Domain}!{arguments.Parameter}".ToLower(), out object nvSetting))
            {
                byte[] settingBuffer = new byte[0x101];

                if (nvSetting is string stringValue)
                {
                    settingBuffer = Encoding.ASCII.GetBytes(stringValue.Length > 0x100 ? stringValue.Substring(0, 0x100) + "\0" : stringValue + "\0");
                }
                else if (nvSetting is int intValue)
                {
                    settingBuffer = BitConverter.GetBytes(intValue);
                }
                else if (nvSetting is bool boolValue)
                {
                    settingBuffer[0] = boolValue ? (byte)1 : (byte)0;
                }
                else
                {
                    throw new NotImplementedException(nvSetting.GetType().Name);
                }

                arguments.Configuration = settingBuffer;
                return NvInternalResult.Success;
            }
            return NvInternalResult.InvalidInput;
        }

        private NvInternalResult EventWait(ref EventWaitArguments arguments)
        {
            return EventWait(ref arguments.Fence, ref arguments.Value, arguments.Timeout, isWaitEventAsyncCmd: false, isWaitEventCmd: true);
        }

        private NvInternalResult EventWaitAsync(ref EventWaitArguments arguments)
        {
            return EventWait(ref arguments.Fence, ref arguments.Value, arguments.Timeout, isWaitEventAsyncCmd: true, isWaitEventCmd: false);
        }

        private NvInternalResult EventRegister(ref uint userEventId)
        {
            if (userEventId >= EventsCount)
                return NvInternalResult.InvalidInput;

            lock (_eventLocks[userEventId])
            {
                NvInternalResult result = EventUnregister(ref userEventId);
                if (result == NvInternalResult.Success)
                {
                    _events[userEventId] = new NvHostEvent(_device.System.HostSyncpoint, userEventId, _device.System);
                }
                return result;
            }
        }

        private NvInternalResult EventUnregister(ref uint userEventId)
        {
            if (userEventId >= EventsCount)
                return NvInternalResult.InvalidInput;

            lock (_eventLocks[userEventId])
            {
                NvHostEvent hostEvent = _events[userEventId];
                if (hostEvent == null)
                    return NvInternalResult.Success;

                if (hostEvent.State == NvHostEventState.Available ||
                    hostEvent.State == NvHostEventState.Cancelled ||
                    hostEvent.State == NvHostEventState.Signaled)
                {
                    hostEvent.CloseEvent(Context);
                    _events[userEventId] = null;
                    _freeEventIndices.Enqueue(userEventId);
                    return NvInternalResult.Success;
                }
                return NvInternalResult.Busy;
            }
        }

        private NvInternalResult EventKill(ref ulong eventMask)
        {
            NvInternalResult finalResult = NvInternalResult.Success;
            for (uint eventId = 0; eventId < EventsCount; eventId++)
            {
                if ((eventMask & (1UL << (int)eventId)) == 0)
                    continue;

                uint currentEventId = eventId;
                NvInternalResult result = EventUnregister(ref currentEventId);
                if (result != NvInternalResult.Success)
                {
                    finalResult = result;
                }
            }
            return finalResult;
        }

        private NvInternalResult EventSignal(ref uint userEventId)
        {
            uint eventId = userEventId & ushort.MaxValue;
            if (eventId >= EventsCount)
                return NvInternalResult.InvalidInput;

            lock (_eventLocks[eventId])
            {
                NvHostEvent hostEvent = _events[eventId];
                if (hostEvent == null)
                    return NvInternalResult.InvalidInput;

                hostEvent.Cancel(_device.Gpu);
                _device.System.HostSyncpoint.UpdateMin(hostEvent.Fence.Id);
                return NvInternalResult.Success;
            }
        }

        private NvInternalResult SyncptReadMinOrMax(ref NvFence arguments, bool max)
        {
            if (arguments.Id >= SynchronizationManager.MaxHardwareSyncpoints)
                return NvInternalResult.InvalidInput;

            arguments.Value = max ? 
                _device.System.HostSyncpoint.ReadSyncpointMaxValue(arguments.Id) : 
                _device.System.HostSyncpoint.ReadSyncpointValue(arguments.Id);
            
            return NvInternalResult.Success;
        }

        private NvInternalResult EventWait(ref NvFence fence, ref uint value, int timeout, bool isWaitEventAsyncCmd, bool isWaitEventCmd)
        {
            if (fence.Id >= SynchronizationManager.MaxHardwareSyncpoints)
                return NvInternalResult.InvalidInput;

            if (_device.System.HostSyncpoint.IsSyncpointExpired(fence.Id, fence.Value))
            {
                value = _device.System.HostSyncpoint.ReadSyncpointMinValue(fence.Id);
                return NvInternalResult.Success;
            }

            uint newCachedSyncpointValue = _device.System.HostSyncpoint.UpdateMin(fence.Id);
            if (_device.System.HostSyncpoint.IsSyncpointExpired(fence.Id, fence.Value))
            {
                value = newCachedSyncpointValue;
                return NvInternalResult.Success;
            }

            if (timeout == 0)
                return NvInternalResult.TryAgain;

            if (!isWaitEventAsyncCmd)
                value = 0;

            NvHostEvent hostEvent = null;
            uint eventIndex = EventsCount;

            if (isWaitEventAsyncCmd)
            {
                eventIndex = value;
                if (eventIndex >= EventsCount)
                    return NvInternalResult.InvalidInput;

                lock (_eventLocks[eventIndex])
                {
                    hostEvent = _events[eventIndex];
                }
            }
            else
            {
                if (!_freeEventIndices.TryDequeue(out eventIndex))
                    return NvInternalResult.InvalidInput;

                lock (_eventLocks[eventIndex])
                {
                    hostEvent = new NvHostEvent(_device.System.HostSyncpoint, eventIndex, _device.System);
                    _events[eventIndex] = hostEvent;
                }
            }

            if (hostEvent == null)
                return NvInternalResult.InvalidInput;

            bool timedOut = hostEvent.Wait(_device.Gpu, fence, timeout);
            if (timedOut)
            {
                value = isWaitEventCmd ? 
                    ((fence.Id & 0xfff) << 16) | 0x10000000 | eventIndex : 
                    (fence.Id << 4) | eventIndex;
                
                return NvInternalResult.TryAgain;
            }
            
            value = fence.Value;
            return NvInternalResult.Success;
        }

        public override void Close()
        {
            Logger.Warning?.Print(LogClass.ServiceNv, "Closing channel");
            
            for (int i = 0; i < EventsCount; i++)
            {
                uint index = (uint)i;
                lock (_eventLocks[index])
                {
                    NvHostEvent evnt = _events[index];
                    if (evnt == null)
                        continue;

                    if (evnt.State == NvHostEventState.Waiting)
                    {
                        evnt.State = NvHostEventState.Cancelling;
                        evnt.Cancel(_device.Gpu);
                    }
                    else if (evnt.State == NvHostEventState.Signaling)
                    {
                        evnt.SignalEvent.Wait(TimeSpan.FromMilliseconds(9));
                    }

                    evnt.CloseEvent(Context);
                    _events[index] = null;
                    _freeEventIndices.Enqueue(index);
                }
            }
        }
    }

    internal class NvHostEvent
    {
        public NvFence Fence { get; }
        public int EventHandle { get; }
        public NvHostEventState State { get; set; }
        public ManualResetEventSlim SignalEvent { get; } = new ManualResetEventSlim(false);
        private readonly Stopwatch _stateTimer = new Stopwatch();

        public NvHostEvent(SynchronizationManager syncManager, uint id, Switch device)
        {
            Fence = new NvFence { Id = id, Value = syncManager.ReadSyncpointMinValue(id) };
            EventHandle = device.System.HandleManager.GenerateHandle(new EventWaitHolder(this));
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
    }
}
