// TouchScreenManager.cs
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.Common;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.TouchScreen;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Input.HLE
{
    /// <summary>
    /// 触屏输入处理接口（平台无关）
    /// </summary>
    public interface ITouchScreenProvider
    {
        /// <summary>
        /// 获取当前所有触控点状态
        /// </summary>
        IEnumerable<TouchPoint> GetTouchPoints();
    }

    /// <summary>
    /// 触屏管理器（HID服务层）
    /// </summary>
    public sealed class TouchScreenManager : IDisposable
    {
        private const int MaxTouchPoints = 10;  // Switch支持的最大触点数
        private const uint ScreenWidth = 1920;  // Switch屏幕宽度
        private const uint ScreenHeight = 1080; // Switch屏幕高度

        private readonly Switch _device;
        private readonly ITouchScreenProvider _touchProvider;
        private readonly ConcurrentDictionary<int, TouchPoint> _activeTouches;
        private readonly object _updateLock = new();

        /// <summary>
        /// 当前活动触点数
        /// </summary>
        public int ActiveTouchCount => _activeTouches.Count;

        public TouchScreenManager(Switch device, ITouchScreenProvider touchProvider)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _touchProvider = touchProvider ?? throw new ArgumentNullException(nameof(touchProvider));
            _activeTouches = new ConcurrentDictionary<int, TouchPoint>();
        }

        /// <summary>
        /// 更新触屏输入状态
        /// </summary>
        public void Update()
        {
            if (!_device.Hid.Touchscreen.IsEnabled)
            {
                ClearAllTouches();
                return;
            }

            lock (_updateLock)
            {
                ProcessTouchEvents();
                UpdateHidState();
            }
        }

        private void ProcessTouchEvents()
        {
            foreach (var touchPoint in _touchProvider.GetTouchPoints())
            {
                if (!IsValidTouchPoint(ref touchPoint))
                {
                    continue;
                }

                switch (touchPoint.Attribute)
                {
                    case TouchAttribute.Start:
                    case TouchAttribute.None:
                        _activeTouches.AddOrUpdate(
                            touchPoint.Id,
                            touchPoint,
                            (id, existing) => UpdateTouchPoint(existing, touchPoint));
                        break;
                    case TouchAttribute.End:
                        _activeTouches.TryRemove(touchPoint.Id, out _);
                        break;
                }
            }
        }

        private void UpdateHidState()
        {
            var touchScreen = _device.Hid.Touchscreen;
            var entries = touchScreen.Entries;

            entries[0].SampleTimestamp++;
            entries[0].NumberOfTouches = (uint)ActiveTouchCount;

            int index = 0;
            foreach (var touch in _activeTouches.Values)
            {
                if (index >= MaxTouchPoints) break;

                entries[0].Touches[index] = new TouchState
                {
                    Position = new Vector2(touch.X, touch.Y),
                    Diameter = new Vector2(touch.DiameterX, touch.DiameterY),
                    Angle = touch.Angle,
                    TouchId = (uint)touch.Id,
                    Attribute = touch.Attribute
                };

                index++;
            }

            // 填充剩余触点状态
            for (; index < MaxTouchPoints; index++)
            {
                entries[0].Touches[index] = new TouchState
                {
                    Attribute = TouchAttribute.Invalid
                };
            }

            touchScreen.UpdateEntries(entries);
        }

        private bool IsValidTouchPoint(ref TouchPoint point)
        {
            return point.X <= ScreenWidth && 
                   point.Y <= ScreenHeight &&
                   point.Id >= 0 &&
                   point.DiameterX > 0 &&
                   point.DiameterY > 0;
        }

        private TouchPoint UpdateTouchPoint(TouchPoint existing, TouchPoint updated)
        {
            return new TouchPoint
            {
                Id = existing.Id,
                X = updated.X,
                Y = updated.Y,
                DiameterX = updated.DiameterX,
                DiameterY = updated.DiameterY,
                Angle = updated.Angle,
                Attribute = existing.Attribute == TouchAttribute.Start ? 
                           TouchAttribute.None : 
                           existing.Attribute
            };
        }

        private void ClearAllTouches()
        {
            _activeTouches.Clear();
            _device.Hid.Touchscreen.Reset();
        }

        public void Dispose()
        {
            ClearAllTouches();
            GC.SuppressFinalize(this);
        }
    }
}
