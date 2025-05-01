using LibHac.Tools.Fs;
using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LibRyujinx.Android
{
    internal class AndroidUIHandler : IHostUIHandler, IDisposable
    {
        private bool _isDisposed;
        private bool _isOkPressed;
        private string? _input;
        private ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public IHostUITheme HostUITheme => throw new NotImplementedException();

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            throw new NotImplementedException();
        }
        // 新增方法：实现 DisplayCabinetDialog
        public void DisplayCabinetDialog(out string userText)
        {
            _input = null;
            _resetEvent.Reset();
            Interop.UpdateUiHandler("Cabinet Dialog",
                "Please provide cabinet information",
                "",
                2,  // 假设类型码 2 对应输入对话框
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            _resetEvent.WaitOne();

            userText = _input ?? "";
            return _isOkPressed;
        }

        // 新增方法：实现 DisplayCabinetMessageDialog
        public bool DisplayCabinetMessageDialog()
        {
            Interop.UpdateUiHandler("Cabinet Message",
                "Cabinet configuration required",
                "",
                1,  // 假设类型码 1 对应消息对话框
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            _resetEvent.WaitOne();
            return _isOkPressed;
        }
        
        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
        {
            Interop.UpdateUiHandler(title ?? "",
                message ?? "",
                "",
                1,
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            return _isOkPressed;
        }

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            _input = null;
            _resetEvent.Reset();
            Interop.UpdateUiHandler("Software Keyboard",
                args.HeaderText ?? "",
                args.GuideText ?? "",
                2,
                args.StringLengthMin,
                args.StringLengthMax,
                args.KeyboardMode,
                args.SubtitleText ?? "",
                args.InitialText ?? "");

            _resetEvent.WaitOne();

            userText = _input ?? "";

            return _isOkPressed;
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            Interop.UpdateUiHandler(title ?? "",
                message ?? "",
                "",
                1,
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            return _isOkPressed;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            string playerCount = args.PlayerCountMin == args.PlayerCountMax ? $"exactly {args.PlayerCountMin}" : $"{args.PlayerCountMin}-{args.PlayerCountMax}";

            string message = $"Application requests **{playerCount}** player(s) with:\n\n"
                           + $"**TYPES:** {args.SupportedStyles}\n\n"
                           + $"**PLAYERS:** {string.Join(", ", args.SupportedPlayers)}\n\n"
                           + (args.IsDocked ? "Docked mode set. `Handheld` is also invalid.\n\n" : "")
                           + "_Please reconfigure Input now and then press OK._";

            return DisplayMessageDialog("Controller Applet", message);
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
           // throw new NotImplementedException();
        }

        internal void SetResponse(bool isOkPressed, string input)
        {
            if (_isDisposed)
                return;
            _isOkPressed = isOkPressed;
            _input = input;
            _resetEvent.Set();
        }

        public void Dispose()
        {
            _isDisposed = true;
        }
    }
}
