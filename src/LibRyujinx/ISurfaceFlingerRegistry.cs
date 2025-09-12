// 在LibRyujinx项目中添加
using Ryujinx.Core;

namespace LibRyujinx
{
    public class LibRyujinxAdapter : ISurfaceFlingerRegistry
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
