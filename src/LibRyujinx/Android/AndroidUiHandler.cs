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
using System.Runtime.InteropServices;
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

        // 修复：实现默认主题
        public IHostUITheme HostUITheme => new DefaultAndroidTheme();

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            // 返回一个空实现防止NotImplementedException
            return new NullDynamicTextInputHandler();
        }

        public bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText)
        {
            // 默认使用第一个按钮作为确认按钮
            string buttonText = buttonsText?.Length > 0 ? buttonsText[0] : "OK";
            
            Interop.UpdateUiHandler(title ?? "",
                message ?? "",
                buttonText,
                1,
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            _resetEvent.WaitOne();

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
                "OK",
                1,
                0,
                0,
                KeyboardMode.Default,
                "",
                "");

            _resetEvent.WaitOne();

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
            // 空实现
        }

        internal void SetResponse(bool isOkPressed, string input)
        {
            try
            {
                if (_isDisposed)
                    return;
                    
                _isOkPressed = isOkPressed;
                _input = input;
                _resetEvent.Set();
            }
            catch (ObjectDisposedException)
            {
                // 安全处理已释放的资源
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            _resetEvent.Set(); // 确保所有等待线程被释放
        }

        // 修正后的空实现类
        private class NullDynamicTextInputHandler : IDynamicTextInputHandler
        {
            // 实现接口要求的属性
            public bool TextProcessingEnabled { get; set; } = false;
            
            // 使用接口要求的精确委托类型声明事件
            public event DynamicTextChangedHandler TextChangedEvent
            {
                add { }
                remove { }
            }
            
            public event KeyPressedHandler KeyPressedEvent
            {
                add { }
                remove { }
            }
            
            public event KeyReleasedHandler KeyReleasedEvent
            {
                add { }
                remove { }
            }

            // 实现两个SetText重载
            public void SetText(string text, int cursorBegin)
            {
                // 空实现
            }

            public void SetText(string text, int cursorBegin, int cursorEnd)
            {
                // 空实现
            }

            public void Dispose()
            {
                // 空实现
            }
        }
        
        // 新增：默认主题实现
        private class DefaultAndroidTheme : IHostUITheme
        {
            public string FontFamily => "Roboto, sans-serif"; // Android 默认字体
            
            // 浅色模式默认颜色
            public ThemeColor DefaultBackgroundColor => new ThemeColor(255, 255, 255, 255); // 白色
            public ThemeColor DefaultForegroundColor => new ThemeColor(0, 0, 0, 255);       // 黑色
            public ThemeColor DefaultBorderColor => new ThemeColor(200, 200, 200, 255);     // 浅灰色
            public ThemeColor SelectionBackgroundColor => new ThemeColor(173, 216, 230, 255); // 浅蓝色
            public ThemeColor SelectionForegroundColor => new ThemeColor(0, 0, 0, 255);     // 黑色
        }
    }
}
