using Ryujinx.Core;
using System;

namespace LibRyujinx
{
    public class SurfaceFlingerRegistryAdapter : ISurfaceFlingerRegistry
    {
        private Action _targetFpsUpdateCallback;
        private static ISurfaceFlinger _surfaceFlingerInstance;
        
        public void RegisterTargetFpsUpdateCallback(Action callback)
        {
            _targetFpsUpdateCallback = callback;
        }

        public void SetSurfaceFlingerInstance(ISurfaceFlinger surfaceFlinger)
        {
            _surfaceFlingerInstance = surfaceFlinger;
            
            // 如果需要，可以在这里调用 LibRyujinx 的静态方法
            // 但最好通过事件或回调机制，而不是直接调用
        }

        public void UpdateSurfaceFlingerTargetFps()
        {
            _targetFpsUpdateCallback?.Invoke();
        }
        
        // 提供一个静态方法来获取 SurfaceFlinger 实例
        public static ISurfaceFlinger GetSurfaceFlingerInstance()
        {
            return _surfaceFlingerInstance;
        }
    }
}
