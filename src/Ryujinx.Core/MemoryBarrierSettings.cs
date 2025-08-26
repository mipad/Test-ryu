using Ryujinx.Common.Logging;

namespace Ryujinx.Core
{
    public static class MemoryBarrierSettings
    {
        private static bool _skipMemoryBarriers = false;

        public static void SetSkipMemoryBarriers(bool skip)
{
    Logger.Info?.Print(LogClass.Emulation, $"SetSkipMemoryBarriers called with: {skip}");
    _skipMemoryBarriers = skip;
    Logger.Info?.Print(LogClass.Emulation, $"Memory barriers {(skip ? "disabled" : "enabled")}");
}

        public static bool GetSkipMemoryBarriers()
        {
            return _skipMemoryBarriers;
        }
    }
}
