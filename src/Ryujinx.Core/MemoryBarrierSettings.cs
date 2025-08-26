
namespace Ryujinx.Core
{
    public static class MemoryBarrierSettings
    {
        private static bool _skipMemoryBarriers = false;

        public static void SetSkipMemoryBarriers(bool skip)
        {
            _skipMemoryBarriers = skip;
        }

        public static bool GetSkipMemoryBarriers()
        {
            return _skipMemoryBarriers;
        }
    }
}
