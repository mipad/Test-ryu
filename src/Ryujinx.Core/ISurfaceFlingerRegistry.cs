// ISurfaceFlingerRegistry.cs
namespace Ryujinx.Core
{
    public interface ISurfaceFlingerRegistry
    {
        void RegisterTargetFpsUpdateCallback(Action callback);
        void UpdateSurfaceFlingerTargetFps();
        void SetSurfaceFlingerInstance(ISurfaceFlinger surfaceFlinger); // 使用接口而不是具体类
    }
}
