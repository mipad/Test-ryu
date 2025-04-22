using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Time.Clock
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SystemClockContext // 添加 public 修饰符
    {
        public long Offset;
        public SteadyClockTimePoint SteadyTimePoint;
    }
}
