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
        private bool _isTouching = false;
        private Vector2 _lastPosition = Vector2.Zero;

        public TouchScreenManager(IMouse mouse)
        {
            _mouse = mouse;
        }

        public void Initialize(Switch device)
        {
            _device = device;
        }

        // 添加设置触摸点的方法
        public void SetTouchPoint(int x, int y)
        {
            _isTouching = true;
            _lastPosition = new Vector2(x, y);
        }

        // 添加释放触摸点的方法
        public void ReleaseTouch()
        {
            _isTouching = false;
        }

        // 修改 Update 方法
        public void Update(float aspectRatio = 0)
        {
            if (aspectRatio <= 0) 
                return;
            
            if (_isTouching)
            {
                // 计算屏幕位置
                var touchPosition = IMouse.GetScreenPosition(_lastPosition, _mouse.ClientSize, aspectRatio);
                
                TouchPoint point = new()
                {
                    Attribute = TouchAttribute.None,
                    X = (uint)touchPosition.X,
                    Y = (uint)touchPosition.Y,
                    DiameterX = 10,
                    DiameterY = 10,
                    Angle = 90,
                    FingerId = 0
                };
                
                _device.Hid.Touchscreen.Update(point);
            }
            else
            {
                // 发送结束触摸事件
                TouchPoint endPoint = new()
                {
                    Attribute = TouchAttribute.End,
                    X = (uint)_lastPosition.X,
                    Y = (uint)_lastPosition.Y,
                    DiameterX = 10,
                    DiameterY = 10,
                    Angle = 90,
                    FingerId = 0
                };
                
                _device.Hid.Touchscreen.Update(endPoint);
            }
            
            _device.Hid.Touchscreen.Update();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
