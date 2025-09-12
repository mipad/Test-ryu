using Ryujinx.Core;

namespace LibRyujinx
{
    public class SurfaceFlingerRegistryAdapter : ISurfaceFlingerRegistry
    {
        public void SetSurfaceFlingerInstance(object surfaceFlinger)
        {
            LibRyujinx.SetSurfaceFlingerInstance(surfaceFlinger as SurfaceFlinger);
        }

        public void UpdateSurfaceFlingerTargetFps()
        {
            LibRyujinx.UpdateSurfaceFlingerTargetFps();
        }
    }
}
