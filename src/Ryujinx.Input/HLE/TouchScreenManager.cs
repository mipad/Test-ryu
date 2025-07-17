using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using System;
using System.Numerics;

namespace Ryujinx.Input.HLE
{
    public class TouchScreenManager : IDisposable
    {
        private readonly IMouse _mouse;
        private Switch _device;
        private Vector2 _lastPosition = Vector2.Zero;
        private TouchState _touchState = TouchState.Released;
        private bool _wasTouching = false;

        public enum TouchState
        {
            Released,
            Started,
            Moving
        }

        public TouchScreenManager(IMouse mouse)
        {
            _mouse = mouse;
        }

        public void Initialize(Switch device)
        {
            _device = device;
        }

        public void SetTouchPoint(int x, int y)
        {
            _lastPosition = new Vector2(x, y);
            
            if (_touchState == TouchState.Released)
            {
                _touchState = TouchState.Started;
            }
            else
            {
                _touchState = TouchState.Moving;
            }
        }

        public void ReleaseTouch()
        {
            _touchState = TouchState.Released;
        }

        public void Update(float aspectRatio = 0)
        {
            if (_device?.Hid?.Touchscreen == null || _device.Hid.Touchscreen.IsInvalid)
                return;
            
            // 确保宽高比有效
            if (aspectRatio <= 0) aspectRatio = 1.0f;
            
            // 使用正确的坐标转换方法
            var touchPosition = IMouse.GetScreenPosition(_lastPosition, _mouse.ClientSize, aspectRatio);
            
            // 检查触摸是否在有效区域内
            bool isValidTouch = touchPosition.X > 0 || touchPosition.Y > 0;
            
            // 处理触摸状态转换
            switch (_touchState)
            {
                case TouchState.Started when isValidTouch:
                    UpdateTouchPoint(touchPosition, TouchAttribute.Start);
                    _touchState = TouchState.Moving;
                    _wasTouching = true;
                    break;
                    
                case TouchState.Moving when isValidTouch:
                    UpdateTouchPoint(touchPosition, TouchAttribute.None);
                    break;
                    
                case TouchState.Released when _wasTouching:
                    UpdateTouchPoint(touchPosition, TouchAttribute.End);
                    _wasTouching = false;
                    break;
            }
            
            // 强制更新 HID 状态
            _device.Hid.Touchscreen.Update();
        }

        private void UpdateTouchPoint(Vector2 position, TouchAttribute attribute)
        {
            TouchPoint point = new TouchPoint
            {
                Attribute = attribute,
                X = (uint)Math.Clamp(position.X, 0, 1280),
                Y = (uint)Math.Clamp(position.Y, 0, 720),
                DiameterX = 10,
                DiameterY = 10,
                Angle = 90
            };
            
            _device.Hid.Touchscreen.Update(point);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
