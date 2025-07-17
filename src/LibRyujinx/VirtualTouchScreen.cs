using Ryujinx.Input;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace LibRyujinx
{
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
}
