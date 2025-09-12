// SurfaceFlingerRegistry.cs
namespace Ryujinx.Core
{
    public class SurfaceFlingerRegistry : ISurfaceFlingerRegistry
    {
        private Action _targetFpsUpdateCallback;
        private ISurfaceFlinger _surfaceFlingerInstance; // 使用接口类型

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
