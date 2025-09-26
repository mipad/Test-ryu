using LibRyujinx.Sample;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Runtime.InteropServices;

namespace LibRyujinx.NativeSample
{
    internal class NativeWindow : OpenTK.Windowing.Desktop.NativeWindow
    {
        private nint del;
        public delegate void SwapBuffersCallback();
        public delegate IntPtr GetProcAddress(string name);
        public delegate IntPtr CreateSurface(IntPtr instance);

        private bool _run;
        private bool _isVulkan;
        private Vector2 _lastPosition;
        private bool _mousePressed;
        private int _playerIndex; // 修改：使用整数玩家索引而不是指针
        private int _controllerType; // 新增：存储当前控制器类型

        public NativeWindow(NativeWindowSettings nativeWindowSettings) : base(nativeWindowSettings)
        {
            _isVulkan = true;
            _controllerType = 1; // 默认使用ProController (1 << 0)
        }

        // 新增方法：设置控制器类型
        public void SetControllerType(int controllerType)
        {
            _controllerType = controllerType;
            // 如果已经连接了游戏手柄，需要重新连接以应用新的控制器类型
            if (_playerIndex != -1)
            {
                DisconnectGamepad();
                ConnectGamepad();
            }
        }

        internal unsafe void Start(string gamePath)
        {
            if (!_isVulkan)
            {
                MakeCurrent();
            }

            var getProcAddress = Marshal.GetFunctionPointerForDelegate<GetProcAddress>(x => GLFW.GetProcAddress(x));
            var createSurface = Marshal.GetFunctionPointerForDelegate<CreateSurface>( x =>
            {
                VkHandle surface;
                GLFW.CreateWindowSurface(new VkHandle(x) ,this.WindowPtr, null, out surface);

                return surface.Handle;
            });
            var vkExtensions = GLFW.GetRequiredInstanceExtensions();

            var pointers = new IntPtr[vkExtensions.Length];
            for (int i = 0; i < vkExtensions.Length; i++)
            {
                pointers[i] = Marshal.StringToHGlobalAnsi(vkExtensions[i]);
            }

            fixed (IntPtr* ptr = pointers)
            {
                var nativeGraphicsInterop = new NativeGraphicsInterop()
                {
                    GlGetProcAddress = getProcAddress,
                    VkRequiredExtensions = (nint)ptr,
                    VkRequiredExtensionsCount = pointers.Length,
                    VkCreateSurface = createSurface
                };
                var success = LibRyujinxInterop.InitializeGraphicsRenderer(_isVulkan ? GraphicsBackend.Vulkan : GraphicsBackend.OpenGl, nativeGraphicsInterop);
                var timeZone = Marshal.StringToHGlobalAnsi("UTC");
                success = LibRyujinxInterop.InitializeDevice(true,
                    false,
                    SystemLanguage.AmericanEnglish,
                    RegionCode.USA,
                    true,
                    true,
                    true,
                    false,
                    timeZone,
                    false);
                LibRyujinxInterop.InitializeInput(ClientSize.X, ClientSize.Y);
                Marshal.FreeHGlobal(timeZone);

                var path = Marshal.StringToHGlobalAnsi(gamePath);
                var loaded = LibRyujinxInterop.LoadApplication(path);
                LibRyujinxInterop.SetRendererSize(Size.X, Size.Y);
                Marshal.FreeHGlobal(path);
            }

            // 修改：根据控制器类型选择玩家索引
            ConnectGamepad();

            if (!_isVulkan)
            {
                Context.MakeNoneCurrent();
            }

            _run = true;
            var thread = new Thread(new ThreadStart(RunLoop));
            thread.Start();

            UpdateLoop();

            thread.Join();

            foreach(var ptr in pointers)
            {
                Marshal.FreeHGlobal(ptr);
            }

            // 修改：不再需要释放指针，因为现在使用整数索引
        }

        // 新增方法：连接游戏手柄
        private void ConnectGamepad()
        {
            // 根据控制器类型决定使用哪个玩家索引
            // 如果是掌机模式（Handheld），使用玩家索引8
            // 否则使用玩家索引0（玩家1）
            int playerIndexToUse = (_controllerType == 2) ? 8 : 0; // 2是Handheld的位掩码值 (1 << 1)
            
            _playerIndex = LibRyujinxInterop.ConnectGamepad(playerIndexToUse);
            
            if (_playerIndex != -1)
            {
                // 设置控制器类型
                LibRyujinxInterop.SetControllerType(_playerIndex, _controllerType);
            }
        }

        // 新增方法：断开游戏手柄连接
        private void DisconnectGamepad()
        {
            if (_playerIndex != -1)
            {
                // 这里可以添加断开连接的逻辑（如果需要）
                _playerIndex = -1;
            }
        }

        public void RunLoop()
        {
            del = Marshal.GetFunctionPointerForDelegate<SwapBuffersCallback>(SwapBuffers);
            LibRyujinxInterop.SetSwapBuffersCallback(del);

            if (!_isVulkan)
            {
                MakeCurrent();

                Context.SwapInterval = 0;
            }

            LibRyujinxInterop.RunLoop();

            _run = false;

            if (!_isVulkan)
            {
                Context.MakeNoneCurrent();
            }
        }

        private void SwapBuffers()
        {
            if (!_isVulkan)
            {
                this.Context.SwapBuffers();
            }
        }

        protected override void OnMouseMove(MouseMoveEventArgs e)
        {
            base.OnMouseMove(e);
            _lastPosition = e.Position;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if(e.Button == MouseButton.Left)
            {
                _mousePressed = true;
            }
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);

            if (_run)
            {
                LibRyujinxInterop.SetRendererSize(e.Width, e.Height);
                LibRyujinxInterop.SetClientSize(e.Width, e.Height);
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButton.Left)
            {
                _mousePressed = false;
            }
        }

        protected override void OnKeyUp(KeyboardKeyEventArgs e)
        {
            base.OnKeyUp(e);

            // 修改：使用整数玩家索引而不是指针
            if (_playerIndex != -1)
            {
                var key = GetKeyMapping(e.Key);
                LibRyujinxInterop.SetButtonReleased(key, _playerIndex);
            }
        }

        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            base.OnKeyDown(e);

            // 修改：使用整数玩家索引而不是指针
            if (_playerIndex != -1)
            {
                var key = GetKeyMapping(e.Key);
                LibRyujinxInterop.SetButtonPressed(key, _playerIndex);
            }
        }

        public void UpdateLoop()
        {
            while(_run)
            {
                ProcessWindowEvents(true);
                NewInputFrame();
                ProcessWindowEvents(IsEventDriven);
                if (_mousePressed)
                {
                    LibRyujinxInterop.SetTouchPoint((int)_lastPosition.X, (int)_lastPosition.Y);
                }
                else
                {
                    LibRyujinxInterop.ReleaseTouchPoint();
                }

                LibRyujinxInterop.UpdateInput();

                Thread.Sleep(1);
            }
        }

        public GamepadButtonInputId GetKeyMapping(Keys key)
        {
            if(_keyMapping.TryGetValue(key, out var mapping))
            {
                return mapping;
            }

            return GamepadButtonInputId.Unbound;
        }

        private Dictionary<Keys, GamepadButtonInputId> _keyMapping = new Dictionary<Keys, GamepadButtonInputId>()
        {
            {Keys.A, GamepadButtonInputId.A },
            {Keys.S, GamepadButtonInputId.B },
            {Keys.Z, GamepadButtonInputId.X },
            {Keys.X, GamepadButtonInputId.Y },
            {Keys.Equal, GamepadButtonInputId.Plus },
            {Keys.Minus, GamepadButtonInputId.Minus },
            {Keys.Q, GamepadButtonInputId.LeftShoulder },
            {Keys.D1, GamepadButtonInputId.LeftTrigger },
            {Keys.W, GamepadButtonInputId.RightShoulder },
            {Keys.D2, GamepadButtonInputId.RightTrigger },
            {Keys.E, GamepadButtonInputId.LeftStick },
            {Keys.R, GamepadButtonInputId.RightStick },
            {Keys.Up, GamepadButtonInputId.DpadUp },
            {Keys.Down, GamepadButtonInputId.DpadDown },
            {Keys.Left, GamepadButtonInputId.DpadLeft },
            {Keys.Right, GamepadButtonInputId.DpadRight },
            {Keys.U, GamepadButtonInputId.SingleLeftTrigger0 },
            {Keys.D7, GamepadButtonInputId.SingleLeftTrigger1 },
            {Keys.O, GamepadButtonInputId.SingleRightTrigger0 },
            {Keys.D9, GamepadButtonInputId.SingleRightTrigger1 }
        };
    }
}
