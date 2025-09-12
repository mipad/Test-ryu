// SurfaceFlingerRegistry.cs (在 Ryujinx.Core 中)
namespace Ryujinx.Core
{
    public class SurfaceFlingerRegistry : ISurfaceFlingerRegistry
    {
        private Action _targetFpsUpdateCallback;
        private ISurfaceFlinger _surfaceFlingerInstance;

        public void RegisterTargetFpsUpdateCallback(Action callback)
        {
            _targetFpsUpdateCallback = callback;
        }

        public void UpdateSurfaceFlingerTargetFps()
        {
            _targetFpsUpdateCallback?.Invoke();
        }

        public void SetSurfaceFlingerInstance(ISurfaceFlinger surfaceFlinger)
        {
            _surfaceFlingerInstance = surfaceFlinger;
        }
    }
}
