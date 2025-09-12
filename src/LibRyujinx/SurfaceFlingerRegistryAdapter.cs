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
            LibRyujinx.SetSurfaceFlingerInstance(surfaceFlinger);
        }

        public void UpdateSurfaceFlingerTargetFps()
        {
            _targetFpsUpdateCallback?.Invoke();
        }
    }
}
