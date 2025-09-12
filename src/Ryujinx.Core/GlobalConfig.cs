using System;

namespace Ryujinx.Core
{
    public static class GlobalConfig
    {
        private static object _lock = new object();
        private static double _fpsScalingFactor = 1.0;

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
    }
}
