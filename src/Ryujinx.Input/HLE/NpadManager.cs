using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Configuration.Hid.Keyboard;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using CemuHookClient = Ryujinx.Input.Motion.CemuHook.Client;
using ControllerType = Ryujinx.Common.Configuration.Hid.ControllerType;
using PlayerIndex = Ryujinx.HLE.HOS.Services.Hid.PlayerIndex;
using Switch = Ryujinx.HLE.Switch;
using Ryujinx.Common.Logging;

namespace Ryujinx.Input.HLE
{
    public class NpadManager : IDisposable
    {
        private readonly CemuHookClient _cemuHookClient;

        private readonly object _lock = new();

        private bool _blockInputUpdates;

        private const int MaxControllers = 9;

        private readonly NpadController[] _controllers;

        private readonly IGamepadDriver _keyboardDriver;
        private readonly IGamepadDriver _gamepadDriver;
        private readonly IGamepadDriver _mouseDriver;
        private bool _isDisposed;

        private List<InputConfig> _inputConfig;
        private bool _enableKeyboard;
        private bool _enableMouse;
        private Switch _device;
        
        // 新增：应用请求的控制器类型
        private List<string> _requestedTypes = new List<string>();
        
        // 新增：控制器类型映射
        private static readonly Dictionary<string, ControllerType> _typeMapping = new()
        {
            ["Handheld"] = ControllerType.Handheld,
            ["JoyconLeft"] = ControllerType.JoyconLeft,
            ["JoyconRight"] = ControllerType.JoyconRight,
            ["Pokeball"] = ControllerType.Pokeball,
            ["ProController"] = ControllerType.ProController
        };

        public NpadManager(IGamepadDriver keyboardDriver, IGamepadDriver gamepadDriver, IGamepadDriver mouseDriver)
        {
            _controllers = new NpadController[MaxControllers];
            _cemuHookClient = new CemuHookClient(this);

            _keyboardDriver = keyboardDriver;
            _gamepadDriver = gamepadDriver;
            _mouseDriver = mouseDriver;
            _inputConfig = new List<InputConfig>();

            _gamepadDriver.OnGamepadConnected += HandleOnGamepadConnected;
            _gamepadDriver.OnGamepadDisconnected += HandleOnGamepadDisconnected;
        }

        // 新增：设置应用请求的控制器类型
        public void SetRequestedControllerTypes(IEnumerable<string> types)
        {
            lock (_lock)
            {
                _requestedTypes = types.ToList();
                Logger.Info?.Print(LogClass.Hid, $"Application requests: {string.Join(", ", _requestedTypes)}");
                
                // 立即重新映射控制器
                RemapControllers();
            }
        }

        private void RefreshInputConfigForHLE()
        {
            lock (_lock)
            {
                List<InputConfig> validInputs = new();
                foreach (var inputConfigEntry in _inputConfig)
                {
                    if (_controllers[(int)inputConfigEntry.PlayerIndex] != null)
                    {
                        validInputs.Add(inputConfigEntry);
                    }
                }

                _device.Hid.RefreshInputConfig(validInputs);
            }
        }

        private void HandleOnGamepadDisconnected(string obj)
        {
            // Force input reload
            lock (_lock)
            {
                // Forcibly disconnect any controllers with this ID.
                for (int i = 0; i < _controllers.Length; i++)
                {
                    if (_controllers[i]?.Id == obj)
                    {
                        _controllers[i]?.Dispose();
                        _controllers[i] = null;
                    }
                }

                ReloadConfiguration(_inputConfig, _enableKeyboard, _enableMouse);
            }
        }

        private void HandleOnGamepadConnected(string id)
        {
            // Force input reload
            ReloadConfiguration(_inputConfig, _enableKeyboard, _enableMouse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool DriverConfigurationUpdate(ref NpadController controller, InputConfig config)
        {
            IGamepadDriver targetDriver = _gamepadDriver;

            if (config is StandardControllerInputConfig)
            {
                targetDriver = _gamepadDriver;
            }
            else if (config is StandardKeyboardInputConfig)
            {
                targetDriver = _keyboardDriver;
            }

            Debug.Assert(targetDriver != null, "Unknown input configuration!");

            if (controller.GamepadDriver != targetDriver || controller.Id != config.Id)
            {
                return controller.UpdateDriverConfiguration(targetDriver, config);
            }

            return controller.GamepadDriver != null;
        }

        public void ReloadConfiguration(List<InputConfig> inputConfig, bool enableKeyboard, bool enableMouse)
        {
            lock (_lock)
            {
                NpadController[] oldControllers = _controllers.ToArray();

                List<InputConfig> validInputs = new();

                foreach (InputConfig inputConfigEntry in inputConfig)
                {
                    NpadController controller;
                    int index = (int)inputConfigEntry.PlayerIndex;

                    if (oldControllers[index] != null)
                    {
                        // Try reuse the existing controller.
                        controller = oldControllers[index];
                        oldControllers[index] = null;
                    }
                    else
                    {
                        controller = new(_cemuHookClient);
                    }

                    bool isValid = DriverConfigurationUpdate(ref controller, inputConfigEntry);

                    if (!isValid)
                    {
                        _controllers[index] = null;
                        controller.Dispose();
                    }
                    else
                    {
                        _controllers[index] = controller;
                        validInputs.Add(inputConfigEntry);
                    }
                }

                for (int i = 0; i < oldControllers.Length; i++)
                {
                    // Disconnect any controllers that weren't reused by the new configuration.

                    oldControllers[i]?.Dispose();
                    oldControllers[i] = null;
                }

                _inputConfig = inputConfig;
                _enableKeyboard = enableKeyboard;
                _enableMouse = enableMouse;

                _device.Hid.RefreshInputConfig(validInputs);
                
                // 关键修复：重新映射控制器
                RemapControllers();
            }
        }

        // 新增：重新映射控制器以匹配请求
        private void RemapControllers()
        {
            if (_requestedTypes == null || !_requestedTypes.Any()) 
                return;
            
            // 1. 创建控制器类型映射表
            var requestedTypes = _requestedTypes.ToList();
            
            Logger.Info?.Print(LogClass.Hid, 
                $"Remapping controllers for request: {string.Join(", ", requestedTypes)}");
            
            // 2. 遍历所有控制器
            for (int i = 0; i < _controllers.Length; i++)
            {
                var controller = _controllers[i];
                if (controller == null) 
                    continue;
                
                // 3. 检查当前控制器类型是否匹配请求
                var controllerType = controller.ControllerType;
                var mappedType = MapToRequestedType(controllerType);
                
                if (requestedTypes.Contains(mappedType))
                {
                    // 匹配成功，从请求列表中移除
                    requestedTypes.Remove(mappedType);
                    Logger.Info?.Print(LogClass.Hid, 
                        $"Controller {i} ({controllerType}) matches requested type: {mappedType}");
                }
                else if (requestedTypes.Any())
                {
                    // 4. 如果不匹配，尝试重新映射
                    var newType = MapRequestedType(requestedTypes.First());
                    if (newType != ControllerType.None)
                    {
                        controller.SetControllerType(newType);
                        requestedTypes.RemoveAt(0);
                        Logger.Info?.Print(LogClass.Hid, 
                            $"Remapped controller {i} to {newType}");
                    }
                }
            }
            
            // 5. 记录未满足的请求
            if (requestedTypes.Any())
            {
                Logger.Warning?.Print(LogClass.Hid, 
                    $"No controllers available for requested types: {string.Join(", ", requestedTypes)}");
            }
        }
        
        // 新增：将控制器类型映射到请求类型
        private string MapToRequestedType(ControllerType type)
        {
            return type switch
            {
                ControllerType.Handheld => "Handheld",
                ControllerType.JoyconLeft => "JoyconLeft",
                ControllerType.JoyconRight => "JoyconRight",
                ControllerType.Pokeball => "Pokeball",
                ControllerType.ProController => "ProController",
                ControllerType.JoyconPair => "JoyconPair",
                _ => "Unknown"
            };
        }
        
        // 新增：将请求类型映射到控制器类型
        private ControllerType MapRequestedType(string requestedType)
        {
            if (_typeMapping.TryGetValue(requestedType, out var controllerType))
            {
                return controllerType;
            }
            
            Logger.Warning?.Print(LogClass.Hid, 
                $"Unknown controller type requested: {requestedType}");
            
            return ControllerType.None;
        }

        public void UnblockInputUpdates()
        {
            lock (_lock)
            {
                foreach (InputConfig inputConfig in _inputConfig)
                {
                    _controllers[(int)inputConfig.PlayerIndex]?.GamepadDriver?.Clear();
                }

                _blockInputUpdates = false;
            }
        }

        public void BlockInputUpdates()
        {
            lock (_lock)
            {
                _blockInputUpdates = true;
            }
        }

        public void Initialize(Switch device, List<InputConfig> inputConfig, bool enableKeyboard, bool enableMouse)
        {
            _device = device;
            _device.Configuration.RefreshInputConfig = RefreshInputConfigForHLE;

            ReloadConfiguration(inputConfig, enableKeyboard, enableMouse);
        }

        public void Update(float aspectRatio = 1)
        {
            lock (_lock)
            {
                // 关键修复：在更新前检查匹配
                if (_requestedTypes != null && _requestedTypes.Any() && !HasMatchingController())
                {
                    Logger.Warning?.Print(LogClass.Hid, 
                        $"No matching controllers found for: {string.Join(", ", _requestedTypes)}");
                }
                
                List<GamepadInput> hleInputStates = new();
                List<SixAxisInput> hleMotionStates = new(NpadDevices.MaxControllers);

                KeyboardInput? hleKeyboardInput = null;

                foreach (InputConfig inputConfig in _inputConfig)
                {
                    GamepadInput inputState = default;
                    (SixAxisInput, SixAxisInput) motionState = default;

                    NpadController controller = _controllers[(int)inputConfig.PlayerIndex];
                    PlayerIndex playerIndex = (PlayerIndex)inputConfig.PlayerIndex;

                    bool isJoyconPair = false;

                    // Do we allow input updates and is a controller connected?
                    if (!_blockInputUpdates && controller != null)
                    {
                        DriverConfigurationUpdate(ref controller, inputConfig);

                        controller.UpdateUserConfiguration(inputConfig);
                        controller.Update();
                        controller.UpdateRumble(_device.Hid.Npads.GetRumbleQueue(playerIndex));

                        inputState = controller.GetHLEInputState();

                        inputState.Buttons |= _device.Hid.UpdateStickButtons(inputState.LStick, inputState.RStick);

                        isJoyconPair = inputConfig.ControllerType == ControllerType.JoyconPair;

                        var altMotionState = isJoyconPair ? controller.GetHLEMotionState(true) : default;

                        motionState = (controller.GetHLEMotionState(), altMotionState);
                    }
                    else
                    {
                        // Ensure that orientation isn't null
                        motionState.Item1.Orientation = new float[9];
                    }

                    inputState.PlayerId = playerIndex;
                    motionState.Item1.PlayerId = playerIndex;

                    hleInputStates.Add(inputState);
                    hleMotionStates.Add(motionState.Item1);

                    if (isJoyconPair && !motionState.Item2.Equals(default))
                    {
                        motionState.Item2.PlayerId = playerIndex;

                        hleMotionStates.Add(motionState.Item2);
                    }
                }

                if (!_blockInputUpdates && _enableKeyboard)
                {
                    hleKeyboardInput = NpadController.GetHLEKeyboardInput(_keyboardDriver);
                }

                _device.Hid.Npads.Update(hleInputStates);
                _device.Hid.Npads.UpdateSixAxis(hleMotionStates);

                if (hleKeyboardInput.HasValue)
                {
                    _device.Hid.Keyboard.Update(hleKeyboardInput.Value);
                }

                if (_enableMouse)
                {
                    var mouse = _mouseDriver.GetGamepad("0") as IMouse;

                    var mouseInput = IMouse.GetMouseStateSnapshot(mouse);

                    uint buttons = 0;

                    if (mouseInput.IsPressed(MouseButton.Button1))
                    {
                        buttons |= 1 << 0;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button2))
                    {
                        buttons |= 1 << 1;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button3))
                    {
                        buttons |= 1 << 2;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button4))
                    {
                        buttons |= 1 << 3;
                    }

                    if (mouseInput.IsPressed(MouseButton.Button5))
                    {
                        buttons |= 1 << 4;
                    }

                    var position = IMouse.GetScreenPosition(mouseInput.Position, mouse.ClientSize, aspectRatio);

                    _device.Hid.Mouse.Update((int)position.X, (int)position.Y, buttons, (int)mouseInput.Scroll.X, (int)mouseInput.Scroll.Y, true);
                }
                else
                {
                    _device.Hid.Mouse.Update(0, 0);
                }

                _device.TamperMachine.UpdateInput(hleInputStates);
            }
        }
        
        // 新增：检查是否有匹配的控制器
        private bool HasMatchingController()
        {
            if (_requestedTypes == null || !_requestedTypes.Any())
                return true;
            
            foreach (var requestedType in _requestedTypes)
            {
                var mappedType = MapRequestedType(requestedType);
                if (mappedType == ControllerType.None) 
                    continue;
                
                foreach (var controller in _controllers)
                {
                    if (controller != null && controller.ControllerType == mappedType)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal InputConfig GetPlayerInputConfigByIndex(int index)
        {
            lock (_lock)
            {
                return _inputConfig.Find(x => x.PlayerIndex == (Common.Configuration.Hid.PlayerIndex)index);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (!_isDisposed)
                    {
                        _cemuHookClient.Dispose();

                        _gamepadDriver.OnGamepadConnected -= HandleOnGamepadConnected;
                        _gamepadDriver.OnGamepadDisconnected -= HandleOnGamepadDisconnected;

                        for (int i = 0; i < _controllers.Length; i++)
                        {
                            _controllers[i]?.Dispose();
                        }

                        _isDisposed = true;
                    }
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
    }
}
