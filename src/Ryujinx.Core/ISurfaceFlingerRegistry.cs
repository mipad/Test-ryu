// ISurfaceFlingerRegistry.cs (在 Ryujinx.Core 中)
using System;

namespace Ryujinx.Core
{
    public interface ISurfaceFlingerRegistry
    {
        void RegisterTargetFpsUpdateCallback(Action callback);
        void UpdateSurfaceFlingerTargetFps();
    }
}
