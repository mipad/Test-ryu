using Ryujinx.Core;
using System;

namespace LibRyujinx
{
    public class SurfaceFlingerRegistryAdapter : ISurfaceFlingerRegistry
    {
        private Action _targetFpsUpdateCallback;
        
        public void RegisterTargetFpsUpdateCallback(Action callback)
        {
            _targetFpsUpdateCallback = callback;
        }

        public void SetSurfaceFlingerInstance(ISurfaceFlinger surfaceFlinger)
        {
            // 通过接口而不是具体类来传递实例
            // 这里假设 LibRyujinx 有一个接受 ISurfaceFlinger 的方法
            LibRyujinx.SetSurfaceFlingerInstance(surfaceFlinger);
        }

        public void UpdateSurfaceFlingerTargetFps()
        {
            // 调用注册的回调，而不是直接调用 LibRyujinx 的方法
            _targetFpsUpdateCallback?.Invoke();
        }
    }
}
