using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Controller.Motion;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ConfigGamepadInputId = Ryujinx.Common.Configuration.Hid.Controller.GamepadInputId;
using ConfigStickInputId = Ryujinx.Common.Configuration.Hid.Controller.StickInputId;
using StickInputId = Ryujinx.Input.StickInputId;

namespace LibRyujinx
{
    public static partial class LibRyujinx
    {
        private static VirtualGamepadDriver? _gamepadDriver;
        private static VirtualTouchScreen? _virtualTouchScreen;
        private static VirtualTouchScreenDriver? _touchScreenDriver;
        private static TouchScreenManager? _touchScreenManager;
        private static InputManager? _inputManager;
        private static NpadManager? _npadManager;
        private static InputConfig[] _configs;

        public static void InitializeInput(int width, int height)
        {
            if(SwitchDevice!.InputManager != null)
            {
                throw new InvalidOperationException("Input is already initialized");
            }

            _gamepadDriver = new VirtualGamepadDriver(4);
            _configs = new InputConfig[4];
            _virtualTouchScreen = new VirtualTouchScreen();
            
            // 修复1：立即设置触摸屏尺寸
            _virtualTouchScreen.ClientSize = new Size(width, height);
            
            _touchScreenDriver = new VirtualTouchScreenDriver(_virtualTouchScreen);
            _inputManager = new InputManager(null, _gamepadDriver);
            
            // 修复2：明确设置触摸屏为鼠标设备
            _inputManager.SetMouseDriver(_touchScreenDriver);
            
            _npadManager = _inputManager.CreateNpadManager();

            SwitchDevice!.InputManager = _inputManager;

            _touchScreenManager = _inputManager.CreateTouchScreenManager();
            
            // 修复3：添加空指针保护
            if (SwitchDevice!.EmulationContext != null)
            {
                _touchScreenManager.Initialize(SwitchDevice.EmulationContext);
            }
            else
            {
                Logger.Error?.PrintMsg(LogClass.Application, "EmulationContext is null during touch screen init");
            }

            _npadManager.Initialize(SwitchDevice.EmulationContext, new List<InputConfig>(), false, false);
        }

        public static void SetClientSize(int width, int height)
        {
            _virtualTouchScreen!.ClientSize = new Size(width, height);
        }

        public static void SetTouchPoint(int x, int y)
        {
            _virtualTouchScreen?.SetPosition(x, y);
        }

        public static void ReleaseTouchPoint()
        {
            _virtualTouchScreen?.ReleaseTouch();
        }

        public static void SetButtonPressed(GamepadButtonInputId button, int id)
        {
            _gamepadDriver?.SetButtonPressed(button, id);
        }

        public static void SetButtonReleased(GamepadButtonInputId button, int id)
        {
            _gamepadDriver?.SetButtonReleased(button, id);
        }

        public static void SetAccelerometerData(Vector3 accel, int id)
        {
            _gamepadDriver?.SetAccelerometerData(accel, id);
        }

        public static void SetGryoData(Vector3 gyro, int id)
        {
            _gamepadDriver?.SetGryoData(gyro, id);
        }

        public static void SetStickAxis(StickInputId stick, Vector2 axes, int deviceId)
        {
            _gamepadDriver?.SetStickAxis(stick, axes, deviceId);
        }

        public static int ConnectGamepad(int index)
        {
            var gamepad = _gamepadDriver?.GetGamepad(index);
            if (gamepad != null)
            {
                var config = CreateDefaultInputConfig();

                config.Id = gamepad.Id;
                config.PlayerIndex = (PlayerIndex)index;

                _configs[index] = config;
            }

            _npadManager?.ReloadConfiguration(_configs.Where(x => x != null).ToList(), false, false);

            return int.TryParse(gamepad?.Id, out var idInt) ? idInt : -1;
        }

        private static InputConfig CreateDefaultInputConfig()
        {
            return new StandardControllerInputConfig
            {
                Version = InputConfig.CurrentVersion,
                Backend = InputBackendType.GamepadSDL2,
                Id = null,
                ControllerType = ControllerType.ProController,
                DeadzoneLeft = 0.1f,
                DeadzoneRight = 0.1f,
                RangeLeft = 1.0f,
                RangeRight = 1.0f,
                TriggerThreshold = 0.5f,
                LeftJoycon = new LeftJoyconCommonConfig<ConfigGamepadInputId>
                {
                    DpadUp = ConfigGamepadInputId.DpadUp,
                    DpadDown = ConfigGamepadInputId.DpadDown,
                    DpadLeft = ConfigGamepadInputId.DpadLeft,
                    DpadRight = ConfigGamepadInputId.DpadRight,
                    ButtonMinus = ConfigGamepadInputId.Minus,
                    ButtonL = ConfigGamepadInputId.LeftShoulder,
                    ButtonZl = ConfigGamepadInputId.LeftTrigger,
                    ButtonSl = ConfigGamepadInputId.Unbound,
                    ButtonSr = ConfigGamepadInputId.Unbound,
                },

                LeftJoyconStick = new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>
                {
                    Joystick = ConfigStickInputId.Left,
                    StickButton = ConfigGamepadInputId.LeftStick,
                    InvertStickX = false,
                    InvertStickY = false,
                    Rotate90CW = false,
                },

                RightJoycon = new RightJoyconCommonConfig<ConfigGamepadInputId>
                {
                    ButtonA = ConfigGamepadInputId.A,
                    ButtonB = ConfigGamepadInputId.B,
                    ButtonX = ConfigGamepadInputId.X,
                    ButtonY = ConfigGamepadInputId.Y,
                    ButtonPlus = ConfigGamepadInputId.Plus,
                    ButtonR = ConfigGamepadInputId.RightShoulder,
                    ButtonZr = ConfigGamepadInputId.RightTrigger,
                    ButtonSl = ConfigGamepadInputId.Unbound,
                    ButtonSr = ConfigGamepadInputId.Unbound,
                },

                RightJoyconStick = new JoyconConfigControllerStick<ConfigGamepadInputId, ConfigStickInputId>
                {
                    Joystick = ConfigStickInputId.Right,
                    StickButton = ConfigGamepadInputId.RightStick,
                    InvertStickX = false,
                    InvertStickY = false,
                    Rotate90CW = false,
                },

                Motion = new StandardMotionConfigController
                {
                    MotionBackend = MotionInputBackendType.GamepadDriver,
                    EnableMotion = true,
                    Sensitivity = 100,
                    GyroDeadzone = 1,
                },
                Rumble = new RumbleConfigController
                {
                    StrongRumble = 1f,
                    WeakRumble = 1f,
                    EnableRumble = false
                }
            };
        }

        public static void UpdateInput()
        {
            _npadManager?.Update(GraphicsConfiguration.AspectRatio.ToFloat());

            // 修复4：确保触摸屏事件优先处理
            if (_virtualTouchScreen != null && _virtualTouchScreen.IsButtonPressed(MouseButton.Button1))
            {
                var position = _virtualTouchScreen.GetPosition();
                _touchScreenManager?.UpdateSingleTouch(position.X, position.Y);
            }
            
            if(!_touchScreenManager!.Update(true, 
                _virtualTouchScreen?.IsButtonPressed(MouseButton.Button1) ?? false, 
                GraphicsConfiguration.AspectRatio.ToFloat()))
            {
                SwitchDevice!.EmulationContext?.Hid.Touchscreen.Update();
            }
        }
    }

    public class VirtualTouchScreen : IMouse
    {
        public Size ClientSize { get; set; }

        public bool[] Buttons { get; }

        // 修复6：添加多点触控支持
        private Dictionary<int, Vector2> _activeTouches = new Dictionary<int, Vector2>();
        private int _primaryTouchId = 0;

        public VirtualTouchScreen()
        {
            Buttons = new bool[2];
        }

        public Vector2 CurrentPosition { get; private set; }
        public Vector2 Scroll { get; private set; }
        public string Id => "0";
        public string Name => "AvaloniaMouse";

        public bool IsConnected => true;
        public GamepadFeaturesFlag Features => GamepadFeaturesFlag.None;

        public void Dispose()
        {
            // 清理资源
        }

        public Ryujinx.Input.GamepadStateSnapshot GetMappedStateSnapshot()
        {
            return default;
        }

        public void SetPosition(int x, int y, int touchId = 0)
        {
            _activeTouches[touchId] = new Vector2(x, y);
            _primaryTouchId = touchId;
            Buttons[0] = true;
            CurrentPosition = new Vector2(x, y);
        }

        public void ReleaseTouch(int touchId = 0)
        {
            _activeTouches.Remove(touchId);
            if (_activeTouches.Count == 0)
            {
                Buttons[0] = false;
            }
            else
            {
                // 切换到其他活动触摸点
                _primaryTouchId = _activeTouches.Keys.First();
                CurrentPosition = _activeTouches[_primaryTouchId];
            }
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            return Vector3.Zero;
        }

        public Vector2 GetPosition()
        {
            return CurrentPosition;
        }

        public Vector2 GetScroll()
        {
            return Scroll;
        }

        public Ryujinx.Input.GamepadStateSnapshot GetStateSnapshot()
        {
            return default;
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            return (0, 0);
        }

        public bool IsButtonPressed(MouseButton button)
        {
            return button == MouseButton.Button1 && Buttons[0];
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            return false;
        }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            // 无需实现
        }

        public void SetConfiguration(InputConfig configuration)
        {
            // 无需实现
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            // 无需实现
        }
    }

    public class VirtualTouchScreenDriver : IGamepadDriver
    {
        private readonly VirtualTouchScreen _virtualTouchScreen;

        public VirtualTouchScreenDriver(VirtualTouchScreen virtualTouchScreen)
        {
            _virtualTouchScreen = virtualTouchScreen;
        }

        public string DriverName => "VirtualTouchDriver";

        // 修复7：防止触摸屏被识别为手柄
        public ReadOnlySpan<string> GamepadsIds => Array.Empty<string>();

        public event Action<string> OnGamepadConnected
        {
            add { }
            remove { }
        }

        public event Action<string> OnGamepadDisconnected
        {
            add { }
            remove { }
        }

        public void Dispose()
        {
            // 清理资源
        }

        public IGamepad GetGamepad(string id)
        {
            return _virtualTouchScreen;
        }
    }

    public class VirtualGamepadDriver : IGamepadDriver
    {
        private readonly int _controllerCount;

        public ReadOnlySpan<string> GamepadsIds => _gamePads.Keys.Select(x => x.ToString()).ToArray();

        public string DriverName => "Virtual";

        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        private Dictionary<int, VirtualGamepad> _gamePads;

        public VirtualGamepadDriver(int controllerCount)
        {
            _gamePads = new Dictionary<int, VirtualGamepad>();
            for (int joystickIndex = 0; joystickIndex < controllerCount; joystickIndex++)
            {
                HandleJoyStickConnected(joystickIndex);
            }

            _controllerCount = controllerCount;
        }

        private void HandleJoyStickConnected(int joystickDeviceId)
        {
            _gamePads[joystickDeviceId] = new VirtualGamepad(this, joystickDeviceId);
            OnGamepadConnected?.Invoke(joystickDeviceId.ToString());
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 模拟断开所有连接
                var ids = GamepadsIds;
                foreach (string id in ids)
                {
                    OnGamepadDisconnected?.Invoke(id);
                }

                _gamePads.Clear();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public IGamepad GetGamepad(string id)
        {
            if (int.TryParse(id, out int idInt))
            {
                return _gamePads.TryGetValue(idInt, out var gamePad) ? gamePad : null;
            }
            return null;
        }

        public IGamepad GetGamepad(int index)
        {
            return _gamePads.TryGetValue(index, out var gamePad) ? gamePad : null;
        }

        public void SetStickAxis(StickInputId stick, Vector2 axes, int deviceId)
        {
            if(_gamePads.TryGetValue(deviceId, out var gamePad))
            {
                gamePad.StickInputs[(int)stick] = axes;
            }
        }

        public void SetButtonPressed(GamepadButtonInputId button, int deviceId)
        {
            if (_gamePads.TryGetValue(deviceId, out var gamePad))
            {
                gamePad.ButtonInputs[(int)button] = true;
            }
        }

        public void SetButtonReleased(GamepadButtonInputId button, int deviceId)
        {
            if (_gamePads.TryGetValue(deviceId, out var gamePad))
            {
                gamePad.ButtonInputs[(int)button] = false;
            }
        }

        public void SetAccelerometerData(Vector3 accel, int deviceId)
        {
            if (_gamePads.TryGetValue(deviceId, out var gamePad))
            {
                gamePad.Accelerometer = accel;
            }
        }

        public void SetGryoData(Vector3 gyro, int deviceId)
        {
            if (_gamePads.TryGetValue(deviceId, out var gamePad))
            {
                gamePad.Gyro = gyro;
            }
        }
    }

    public class VirtualGamepad : IGamepad
    {
        private readonly VirtualGamepadDriver _driver;

        private bool[] _buttonInputs;

        private Vector2[] _stickInputs;

        public VirtualGamepad(VirtualGamepadDriver driver, int id)
        {
            _buttonInputs = new bool[(int)GamepadButtonInputId.Count];
            _stickInputs = new Vector2[(int)StickInputId.Count];
            _driver = driver;
            Id = id.ToString();
            IdInt = id;
            IsConnected = true;
        }

        public void Dispose() { }

        public GamepadFeaturesFlag Features { get; } = GamepadFeaturesFlag.Motion;
        public string Id { get; }

        internal readonly int IdInt;

        public string Name => $"Virtual Gamepad {Id}";
        public bool IsConnected { get; private set; } = true;
        public Vector2[] StickInputs { get => _stickInputs; set => _stickInputs = value; }
        public bool[] ButtonInputs { get => _buttonInputs; set => _buttonInputs = value; }
        public Vector3 Accelerometer { get; internal set; }
        public Vector3 Gyro { get; internal set; }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            return _buttonInputs[(int)inputId];
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            var v = _stickInputs[(int)inputId];
            return (v.X, v.Y);
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            if (inputId == MotionInputId.Accelerometer)
                return Accelerometer;
            else if (inputId == MotionInputId.Gyroscope)
                return RadToDegree(Gyro);
            return Vector3.Zero;
        }

        private static Vector3 RadToDegree(Vector3 rad)
        {
            return rad * (180 / MathF.PI);
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            // 无需实现
        }

        public void SetConfiguration(InputConfig configuration)
        {
            // 无需实现
        }

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            // 无需实现
        }

        public Ryujinx.Input.GamepadStateSnapshot GetMappedStateSnapshot()
        {
            Ryujinx.Input.GamepadStateSnapshot result = default;

            foreach (var button in Enum.GetValues<GamepadButtonInputId>())
            {
                if (button != GamepadButtonInputId.Count && !result.IsPressed(button))
                {
                    result.SetPressed(button, IsPressed(button));
                }
            }

            (float leftStickX, float leftStickY) = GetStick(StickInputId.Left);
            (float rightStickX, float rightStickY) = GetStick(StickInputId.Right);

            result.SetStick(StickInputId.Left, leftStickX, leftStickY);
            result.SetStick(StickInputId.Right, rightStickX, rightStickY);

            return result;
        }

        public Ryujinx.Input.GamepadStateSnapshot GetStateSnapshot()
        {
            return default;
        }
    }

    public class TouchScreenManager : IDisposable
    {
        private readonly IMouse _mouse;
        private Switch _device;
        private bool _wasClicking;

        public TouchScreenManager(IMouse mouse)
        {
            _mouse = mouse;
        }

        public void Initialize(Switch device)
        {
            _device = device;
        }
        
        // 添加 UpdateSingleTouch 方法实现
        public void UpdateSingleTouch(float x, float y)
        {
            if (_device?.EmulationContext?.Hid?.Touchscreen == null) return;
            
            var aspectRatio = GraphicsConfiguration.AspectRatio.ToFloat();
            var touchPosition = IMouse.GetScreenPosition(new Vector2(x, y), _mouse.ClientSize, aspectRatio);

            var currentPoint = new TouchPoint
            {
                Attribute = _wasClicking ? TouchAttribute.None : TouchAttribute.Start,
                X = (uint)touchPosition.X,
                Y = (uint)touchPosition.Y,
                DiameterX = 10,
                DiameterY = 10,
                Angle = 90,
            };

            _device.Hid.Touchscreen.Update(currentPoint);
            _wasClicking = true;
        }

        public bool Update(bool isFocused, bool isClicking = false, float aspectRatio = 0)
        {
            if (_device?.EmulationContext?.Hid?.Touchscreen == null)
            {
                return false;
            }

            if (!isFocused || (!_wasClicking && !isClicking))
            {
                if (_wasClicking && !isClicking)
                {
                    MouseStateSnapshot snapshot = IMouse.GetMouseStateSnapshot(_mouse);
                    var touchPosition = IMouse.GetScreenPosition(snapshot.Position, _mouse.ClientSize, aspectRatio);

                    TouchPoint currentPoint = new()
                    {
                        Attribute = TouchAttribute.End,
                        X = (uint)touchPosition.X,
                        Y = (uint)touchPosition.Y,
                        DiameterX = 10,
                        DiameterY = 10,
                        Angle = 90,
                    };

                    _device.Hid.Touchscreen.Update(currentPoint);
                }

                _wasClicking = false;
                _device.Hid.Touchscreen.Update();
                return false;
            }

            if (aspectRatio > 0)
            {
                MouseStateSnapshot snapshot = IMouse.GetMouseStateSnapshot(_mouse);
                var touchPosition = IMouse.GetScreenPosition(snapshot.Position, _mouse.ClientSize, aspectRatio);

                TouchAttribute attribute = TouchAttribute.None;

                if (!_wasClicking && isClicking)
                {
                    attribute = TouchAttribute.Start;
                }
                else if (_wasClicking && !isClicking)
                {
                    attribute = TouchAttribute.End;
                }

                TouchPoint currentPoint = new()
                {
                    Attribute = attribute,
                    X = (uint)touchPosition.X,
                    Y = (uint)touchPosition.Y,
                    DiameterX = 10,
                    DiameterY = 10,
                    Angle = 90,
                };

                _device.Hid.Touchscreen.Update(currentPoint);
                _wasClicking = isClicking;
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
