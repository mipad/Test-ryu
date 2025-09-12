// GlobalConfig.cs
using System;

namespace Ryujinx.Core
{
    public static class GlobalConfig
    {
        private static object _lock = new object();
        private static double _fpsScalingFactor = 1.0;
        private static int _baseTargetFps = 60; // 添加基础帧率配置

        public static double FpsScalingFactor
        {
            get
            {
                lock (_lock)
                {
                    return _fpsScalingFactor;
                }
            }
            set
            {
                lock (_lock)
                {
                    _fpsScalingFactor = value;
                }
            }
        }

        // 添加基础帧率属性
        public static int BaseTargetFps
        {
            get
            {
                lock (_lock)
                {
                    return _baseTargetFps;
                }
            }
            set
            {
                lock (_lock)
                {
                    _baseTargetFps = value;
                }
            }
        }

        public static ISurfaceFlingerRegistry SurfaceFlingerRegistry { get; set; }
    }
}
