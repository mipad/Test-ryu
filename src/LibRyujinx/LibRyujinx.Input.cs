// LibRyujinx.Input.cs
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
        private static InputManager? _inputManager;
        private static NpadManager? _npadManager;
        private static TouchScreenManager? _touchScreenManager;
        private static InputConfig[] _configs;
        private static float _aspectRatio = 1.0f;

        public static void InitializeInput(int width, int height)
        {
            if (SwitchDevice!.InputManager != null)
            {
                throw new InvalidOperationException("Input is already initialized");
            }

            _gamepadDriver = new VirtualGamepadDriver(4);
            _configs = new InputConfig[4];
            _virtualTouchScreen = new VirtualTouchScreen();
            
            _aspectRatio = width > 0 && height > 0 ? (float)width / height : 1.0f;
            _virtualTouchScreen.ClientSize = new Size(width, height);
            
            _touchScreenDriver = new VirtualTouchScreenDriver(_virtualTouchScreen);
            _inputManager = new InputManager(null, _gamepadDriver);
            
            _inputManager.SetMouseDriver(_touchScreenDriver);
            
            _npadManager = _inputManager.CreateNpadManager();
            SwitchDevice!.InputManager = _inputManager;

            _touchScreenManager = _inputManager.CreateTouchScreenManager();
            
            if (SwitchDevice!.EmulationContext != null)
            {
                _touchScreenManager.Initialize(SwitchDevice.EmulationContext);
                // 设置触摸屏尺寸
                _touchScreenManager.SetSize(width, height);
            }
            else
            {
                Logger.Error?.PrintMsg(LogClass.Application, "EmulationContext is null during touch screen init");
            }

            _npadManager.Initialize(SwitchDevice.EmulationContext, new List<InputConfig>(), false, false);
        }

        public static void SetClientSize(int width, int height)
        {
            if (_virtualTouchScreen != null)
            {
                _virtualTouchScreen.ClientSize = new Size(width, height);
                _aspectRatio = width > 0 && height > 0 ? (float)width / height : 1.0f;
                
                // 更新触摸屏管理器尺寸
                _touchScreenManager?.SetSize(width, height);
            }
        }

        public static void SetTouchPoint(int x, int y)
        {
            _virtualTouchScreen?.SetPosition(x, y);
            _touchScreenManager?.SetTouchPoint(x, y); // 通知触摸屏管理器
        }

        public static void ReleaseTouchPoint()
        {
            _virtualTouchScreen?.ReleaseTouch();
            _touchScreenManager?.ReleaseTouch(); // 通知触摸屏管理器
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

        public static void SetGyroData(Vector3 gyro, int id)
        {
            _gamepadDriver?.SetGyroData(gyro, id);
        }

        public static void SetStickAxis(StickInputId stick, Vector2 axes, int deviceId)
        {
            _gamepadDriver?.SetStickAxis(stick, axes, deviceId);
        }

        public static int ConnectGamepad(int index)
        {
            if (index < 0 || index >= _configs.Length) 
                return -1;

            var gamepad = _gamepadDriver?.GetGamepad(index);
            if (gamepad != null)
            {
                var config = CreateDefaultInputConfig();
                config.Id = gamepad.Id;
                config.PlayerIndex = (Ryujinx.Common.Configuration.Hid.PlayerIndex)index;
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
                ControllerType = Ryujinx.Common.Configuration.Hid.ControllerType.ProController,
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
            _npadManager?.Update(_aspectRatio);
            _touchScreenManager?.Update(_aspectRatio); // 更新触摸屏状态，传递宽高比
            
            if (SwitchDevice?.EmulationContext?.Hid != null)
            {
                SwitchDevice.EmulationContext.Hid.Touchscreen.Update();
            }
        }
    }

    public class VirtualTouchScreen : IMouse
    {
        public Size ClientSize { get; set; }
        public bool[] Buttons { get; }
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

        public void Dispose() { }

        public Ryujinx.Input.GamepadStateSnapshot GetMappedStateSnapshot() => default;

        public void SetPosition(int x, int y, int touchId = 0)
        {
            _activeTouches[touchId] = new Vector2(x, y);
            _primaryTouchId = touchId;
            Buttons[0] = true;
            CurrentPosition = new Vector2(x, y);
        }

        public void ReleaseTouch(int touchId = 0)
        {
            if (_activeTouches.Remove(touchId))
            {
                if (_activeTouches.Count == 0)
                {
                    Buttons[0] = false;
                }
                else
                {
                    _primaryTouchId = _activeTouches.Keys.First();
                    CurrentPosition = _activeTouches[_primaryTouchId];
                }
            }
        }

        public void SetSize(int width, int height)
        {
            ClientSize = new Size(width, height);
        }

        public Vector3 GetMotionData(MotionInputId inputId) => Vector3.Zero;
        public Vector2 GetPosition() => CurrentPosition;
        public Vector2 GetScroll() => Scroll;
        public Ryujinx.Input.GamepadStateSnapshot GetStateSnapshot() => default;

        public (float, float) GetStick(StickInputId inputId) => (0, 0);

        public bool IsButtonPressed(MouseButton button)
            => button == MouseButton.Button1 && Buttons[0];

        public bool IsPressed(GamepadButtonInputId inputId) => false;

        public void Rumble(float lowFrequency, float highFrequency, uint durationMs) { }
        public void SetConfiguration(InputConfig configuration) { }
        public void SetTriggerThreshold(float triggerThreshold) { }
    }

    public class VirtualTouchScreenDriver : IGamepadDriver
    {
        private readonly VirtualTouchScreen _virtualTouchScreen;

        public VirtualTouchScreenDriver(VirtualTouchScreen virtualTouchScreen)
        {
            _virtualTouchScreen = virtualTouchScreen;
        }

        public string DriverName => "VirtualTouchDriver";
        public ReadOnlySpan<string> GamepadsIds => Array.Empty<string>();
        public event Action<string> OnGamepadConnected { add { } remove { } }
        public event Action<string> OnGamepadDisconnected { add { } remove { } }

        public void Dispose() { }
        public IGamepad GetGamepad(string id) => _virtualTouchScreen;
    }

    public class VirtualGamepadDriver : IGamepadDriver
    {
        private readonly int _controllerCount;
        private Dictionary<int, VirtualGamepad> _gamePads;

        public ReadOnlySpan<string> GamepadsIds => _gamePads.Keys.Select(x => x.ToString()).ToArray();
        public string DriverName => "Virtual";
        public event Action<string> OnGamepadConnected;
        public event Action<string> OnGamepadDisconnected;

        public VirtualGamepadDriver(int controllerCount)
        {
            _gamePads = new Dictionary<int, VirtualGamepad>();
            for (int i = 0; i < controllerCount; i++)
            {
                _gamePads[i] = new VirtualGamepad(this, i);
                OnGamepadConnected?.Invoke(i.ToString());
            }
            _controllerCount = controllerCount;
        }

        public void Dispose()
        {
            foreach (string id in GamepadsIds)
            {
                OnGamepadDisconnected?.Invoke(id);
            }
            _gamePads.Clear();
            GC.SuppressFinalize(this);
        }

        public IGamepad GetGamepad(string id)
            => int.TryParse(id, out int idInt) && _gamePads.TryGetValue(idInt, out var gamePad) ? gamePad : null;

        public IGamepad GetGamepad(int index)
            => _gamePads.TryGetValue(index, out var gamePad) ? gamePad : null;

        public void SetStickAxis(StickInputId stick, Vector2 axes, int deviceId)
        {
            if (_gamePads.TryGetValue(deviceId, out var gamePad))
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

        public void SetGyroData(Vector3 gyro, int deviceId)
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

        public bool[] ButtonInputs { get; set; }
        public Vector2[] StickInputs { get; set; }
        public Vector3 Accelerometer { get; internal set; }
        public Vector3 Gyro { get; internal set; }

        public string Id { get; }
        public int IdInt { get; }
        public string Name => $"Virtual Gamepad {Id}";
        public bool IsConnected { get; private set; } = true;
        public GamepadFeaturesFlag Features { get; } = GamepadFeaturesFlag.Motion;

        public VirtualGamepad(VirtualGamepadDriver driver, int id)
        {
            _driver = driver;
            ButtonInputs = new bool[(int)GamepadButtonInputId.Count];
            StickInputs = new Vector2[(int)StickInputId.Count];
            Id = id.ToString();
            IdInt = id;
        }

        public void Dispose() { }
        public bool IsPressed(GamepadButtonInputId inputId) => ButtonInputs[(int)inputId];

        public (float, float) GetStick(StickInputId inputId)
        {
            var v = StickInputs[(int)inputId];
            return (v.X, v.Y);
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            return inputId switch
            {
                MotionInputId.Accelerometer => Accelerometer,
                MotionInputId.Gyroscope => RadToDegree(Gyro),
                _ => Vector3.Zero
            };
        }

        private static Vector3 RadToDegree(Vector3 rad) 
            => rad * (180 / MathF.PI);

        public void SetTriggerThreshold(float triggerThreshold) { }
        public void SetConfiguration(InputConfig configuration) { }
        public void Rumble(float lowFrequency, float highFrequency, uint durationMs) { }

        public Ryujinx.Input.GamepadStateSnapshot GetMappedStateSnapshot()
        {
            var result = new Ryujinx.Input.GamepadStateSnapshot();
            foreach (GamepadButtonInputId button in Enum.GetValues(typeof(GamepadButtonInputId)))
            {
                if (button != GamepadButtonInputId.Count)
                {
                    result.SetPressed(button, IsPressed(button));
                }
            }

            (float lx, float ly) = GetStick(StickInputId.Left);
            (float rx, float ry) = GetStick(StickInputId.Right);
            result.SetStick(StickInputId.Left, lx, ly);
            result.SetStick(StickInputId.Right, rx, ry);

            return result;
        }

        public Ryujinx.Input.GamepadStateSnapshot GetStateSnapshot() => default;
    }
    
    public static class TouchScreenManagerExtensions
    {
        public static void SetSize(this Ryujinx.Input.HLE.TouchScreenManager manager, int width, int height)
        {
            if (manager != null && manager.GetType().GetField("_mouse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(manager) is VirtualTouchScreen touchScreen)
            {
                touchScreen.SetSize(width, height);
            }
        }
    }
}
