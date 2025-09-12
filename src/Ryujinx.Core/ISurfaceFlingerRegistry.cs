// ISurfaceFlingerRegistry.cs
namespace Ryujinx.Core
{
    public interface ISurfaceFlingerRegistry
    {
        void SetSurfaceFlingerInstance(object surfaceFlinger);
        void UpdateSurfaceFlingerTargetFps();
    }
}
