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

        public IHostUITheme HostUITheme => new AndroidHostUITheme();

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            return new AndroidDynamicTextInputHandler();
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
        {
            _isOkPressed = false;
            _resetEvent.Reset();
            
            Interop.UpdateUiHandler(title ?? "",
                message ?? "",
                "",
                1,
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            _resetEvent.WaitOne(); // 等待用户响应
            
            return _isOkPressed;
        }

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            _input = null;
            _isOkPressed = false;
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
            _isOkPressed = false;
            _resetEvent.Reset();
            
            Interop.UpdateUiHandler(title ?? "",
                message ?? "",
                "",
                1,
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            _resetEvent.WaitOne(); // 等待用户响应
            
            return _isOkPressed;
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            string playerCount = args.PlayerCountMin == args.PlayerCountMax ? 
                $"exactly {args.PlayerCountMin}" : 
                $"{args.PlayerCountMin}-{args.PlayerCountMax}";

            string message = $"Application requests **{playerCount}** player(s) with:\n\n"
                           + $"**TYPES:** {args.SupportedStyles}\n\n"
                           + $"**PLAYERS:** {string.Join(", ", args.SupportedPlayers)}\n\n"
                           + (args.IsDocked ? "Docked mode set. `Handheld` is also invalid.\n\n" : "")
                           + "_Please reconfigure Input now and then press OK._";

            return DisplayMessageDialog("Controller Applet", message);
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            Logger.Info?.Print(LogClass.Application, $"ExecuteProgram requested: {kind}, Value: {value}");
            // Android平台可能需要特殊的程序执行逻辑
            // 暂时记录日志，不实现具体功能
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
            if (!_isDisposed)
            {
                _isDisposed = true;
                _resetEvent?.Dispose();
                _resetEvent = null;
            }
        }
    }

    // 简单的 Android HostUITheme 实现
    internal class AndroidHostUITheme : IHostUITheme
    {
        public string FontFamily => "Roboto"; // Android 默认字体

        public ThemeColor DefaultBackgroundColor => new ThemeColor(1.0f, 1.0f, 1.0f, 1.0f); // 白色背景
        public ThemeColor DefaultForegroundColor => new ThemeColor(1.0f, 0.0f, 0.0f, 0.0f); // 黑色前景
        public ThemeColor DefaultBorderColor => new ThemeColor(1.0f, 0.8f, 0.8f, 0.8f); // 浅灰色边框
        public ThemeColor SelectionBackgroundColor => new ThemeColor(1.0f, 0.2f, 0.4f, 0.8f); // 蓝色选择背景
        public ThemeColor SelectionForegroundColor => new ThemeColor(1.0f, 1.0f, 1.0f, 1.0f); // 白色选择前景
    }

    // 简单的 Android 动态文本输入处理器
    internal class AndroidDynamicTextInputHandler : IDynamicTextInputHandler
    {
        public bool TextProcessingEnabled { get; set; }

        public event DynamicTextChangedHandler TextChangedEvent;
        public event KeyPressedHandler KeyPressedEvent;
        public event KeyReleasedHandler KeyReleasedEvent;

        public void SetText(string text, int cursorBegin)
        {
            // Android平台可能需要调用原生输入法API
            TextChangedEvent?.Invoke(text, cursorBegin, cursorBegin, false);
        }

        public void SetText(string text, int cursorBegin, int cursorEnd)
        {
            // Android平台可能需要调用原生输入法API
            TextChangedEvent?.Invoke(text, cursorBegin, cursorEnd, false);
        }

        public void Dispose()
        {
            // 清理资源
            TextChangedEvent = null;
            KeyPressedEvent = null;
            KeyReleasedEvent = null;
        }

        // 供Android原生代码调用的方法
        internal void OnTextChanged(string text, int selectionStart, int selectionEnd)
        {
            TextChangedEvent?.Invoke(text, selectionStart, selectionEnd, false);
        }

        internal void OnKeyPressed(Ryujinx.Common.Configuration.Hid.Key key)
        {
            KeyPressedEvent?.Invoke(key);
        }

        internal void OnKeyReleased(Ryujinx.Common.Configuration.Hid.Key key)
        {
            KeyReleasedEvent?.Invoke(key);
        }
    }
}
