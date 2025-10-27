using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.Types;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel
{
    internal class NvHostGpuDeviceFile : NvHostChannelDeviceFile
    {
#pragma warning disable IDE0052 // Remove unread private member
        private readonly KEvent _smExceptionBptIntReportEvent;
        private readonly KEvent _smExceptionBptPauseReportEvent;
        private readonly KEvent _errorNotifierEvent;
#pragma warning restore IDE0052

        private int _smExceptionBptIntReportEventHandle;
        private int _smExceptionBptPauseReportEventHandle;
        private int _errorNotifierEventHandle;

        public NvHostGpuDeviceFile(ServiceCtx context, IVirtualMemoryManager memory, ulong owner) : base(context, memory, owner)
        {
            _smExceptionBptIntReportEvent = CreateEvent(context, out _smExceptionBptIntReportEventHandle);
            _smExceptionBptPauseReportEvent = CreateEvent(context, out _smExceptionBptPauseReportEventHandle);
            _errorNotifierEvent = CreateEvent(context, out _errorNotifierEventHandle);

            // 记录事件创建
            Logger.Debug?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile: Created events - ErrorNotifier: {_errorNotifierEventHandle}, ExceptionBptInt: {_smExceptionBptIntReportEventHandle}, ExceptionBptPause: {_smExceptionBptPauseReportEventHandle}");
        }

        private KEvent CreateEvent(ServiceCtx context, out int handle)
        {
            KEvent evnt = new(context.Device.System.KernelContext);

            if (context.Process.HandleTable.GenerateHandle(evnt.ReadableEvent, out handle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            return evnt;
        }

        public override NvInternalResult Ioctl2(NvIoctl command, Span<byte> arguments, Span<byte> inlineInBuffer)
        {
            NvInternalResult result = NvInternalResult.NotImplemented;

            if (command.Type == NvIoctl.NvHostMagic)
            {
                switch (command.Number)
                {
                    case 0x1b:
                        result = CallIoctlMethod<SubmitGpfifoArguments, ulong>(SubmitGpfifoEx, arguments, inlineInBuffer);
                        break;
                }
            }

            return result;
        }

        public override NvInternalResult QueryEvent(out int eventHandle, uint eventId)
        {
            // TODO: accurately represent and implement those events.
            eventHandle = eventId switch
            {
                0x1 => _smExceptionBptIntReportEventHandle,
                0x2 => _smExceptionBptPauseReportEventHandle,
                0x3 => _errorNotifierEventHandle,
                _ => 0,
            };

            // 记录特定句柄的查询
            if (eventHandle == 1671214)
            {
                Logger.Debug?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile.QueryEvent: *** Returning handle 1671214 *** for eventId={eventId}");
            }

            return eventHandle != 0 ? NvInternalResult.Success : NvInternalResult.InvalidInput;
        }

        private NvInternalResult SubmitGpfifoEx(ref SubmitGpfifoArguments arguments, Span<ulong> inlineData)
        {
            // 在GPU命令提交时触发错误通知事件（模拟）
            // TODO: 这应该在实际发生错误时触发，而不是每次都触发
            TriggerErrorNotifierEvent();
            
            return SubmitGpfifo(ref arguments, inlineData);
        }

        /// <summary>
        /// 触发错误通知事件
        /// </summary>
        public void TriggerErrorNotifierEvent()
        {
            if (_errorNotifierEventHandle != 0)
            {
                try 
                {
                    // 记录事件触发
                    Logger.Debug?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile: *** SIGNALING ErrorNotifierEvent *** handle={_errorNotifierEventHandle}");
                    
                    _errorNotifierEvent.WritableEvent.Signal();
                    
                    Logger.Debug?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile: *** SUCCESSFULLY SIGNALED ErrorNotifierEvent *** handle={_errorNotifierEventHandle}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile: Failed to signal ErrorNotifierEvent handle={_errorNotifierEventHandle}, error: {ex.Message}");
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.ServiceNv, "NvHostGpuDeviceFile: Cannot signal ErrorNotifierEvent - handle is 0");
            }
        }

        /// <summary>
        /// 清除错误通知事件
        /// </summary>
        public void ClearErrorNotifierEvent()
        {
            if (_errorNotifierEventHandle != 0)
            {
                try 
                {
                    _errorNotifierEvent.WritableEvent.Clear();
                    Logger.Debug?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile: Cleared ErrorNotifierEvent handle={_errorNotifierEventHandle}");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile: Failed to clear ErrorNotifierEvent handle={_errorNotifierEventHandle}, error: {ex.Message}");
                }
            }
        }

        public override void Close()
        {
            Logger.Debug?.Print(LogClass.ServiceNv, $"NvHostGpuDeviceFile.Close: Closing events - ErrorNotifier: {_errorNotifierEventHandle}");

            if (_smExceptionBptIntReportEventHandle != 0)
            {
                Context.Process.HandleTable.CloseHandle(_smExceptionBptIntReportEventHandle);
                _smExceptionBptIntReportEventHandle = 0;
            }

            if (_smExceptionBptPauseReportEventHandle != 0)
            {
                Context.Process.HandleTable.CloseHandle(_smExceptionBptPauseReportEventHandle);
                _smExceptionBptPauseReportEventHandle = 0;
            }

            if (_errorNotifierEventHandle != 0)
            {
                Context.Process.HandleTable.CloseHandle(_errorNotifierEventHandle);
                _errorNotifierEventHandle = 0;
            }

            base.Close();
        }
    }
}
