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

        // 修复：实现CreateDynamicTextInputHandler方法
        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            Logger.Info?.Print(LogClass.Application, 
                "AndroidUIHandler: Creating dynamic text input handler");
            return new AndroidDynamicTextInputHandler(this);
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

        // 内部类：Android动态文本输入处理器
        private class AndroidDynamicTextInputHandler : IDynamicTextInputHandler
        {
            private readonly AndroidUIHandler _parent;
            private bool _isDisposed;
            
            public event DynamicTextChangedHandler TextChangedEvent;
            public event KeyPressedHandler KeyPressedEvent;
            public event KeyReleasedHandler KeyReleasedEvent;
            
            public bool TextProcessingEnabled { get; set; } = false;
            
            public AndroidDynamicTextInputHandler(AndroidUIHandler parent)
            {
                _parent = parent;
                Logger.Info?.Print(LogClass.Application, 
                    "AndroidDynamicTextInputHandler created (minimal implementation)");
            }
            
            public void SetText(string text, int cursorBegin)
            {
                Logger.Debug?.Print(LogClass.Application, 
                    $"AndroidDynamicTextInputHandler.SetText called: '{text}' at position {cursorBegin}");
                SetText(text, cursorBegin, cursorBegin);
            }
            
            public void SetText(string text, int cursorBegin, int cursorEnd)
            {
                Logger.Debug?.Print(LogClass.Application, 
                    $"AndroidDynamicTextInputHandler.SetText called: '{text}' at range {cursorBegin}-{cursorEnd}");
                
                // 这里可以添加实际处理逻辑，如果需要的话
                // 目前只是记录日志，防止崩溃
            }
            
            public void Dispose()
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;
                    Logger.Info?.Print(LogClass.Application, 
                        "AndroidDynamicTextInputHandler disposed");
                }
            }
        }
    }
}
