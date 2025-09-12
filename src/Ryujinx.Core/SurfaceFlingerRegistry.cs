// SurfaceFlingerRegistry.cs (在 Ryujinx.Core 中)
using System;

namespace Ryujinx.Core
{
    public class SurfaceFlingerRegistry : ISurfaceFlingerRegistry
    {
        private Action _targetFpsUpdateCallback;
        private SurfaceFlinger _surfaceFlingerInstance;

        public void RegisterTargetFpsUpdateCallback(Action callback)
        {
            _targetFpsUpdateCallback = callback;
        }

        public void UpdateSurfaceFlingerTargetFps()
        {
            _targetFpsUpdateCallback?.Invoke();
        }

        public void SetSurfaceFlingerInstance(SurfaceFlinger surfaceFlinger)
        {
            _surfaceFlingerInstance = surfaceFlinger;
        }
    }
}
