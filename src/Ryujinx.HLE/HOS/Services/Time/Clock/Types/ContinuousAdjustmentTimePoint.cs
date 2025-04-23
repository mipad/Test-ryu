using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Time.Clock.Types
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct ContinuousAdjustmentTimePoint
    {
        [FieldOffset(0)]  public ulong ClockOffset;
        [FieldOffset(8)]  public uint Multiplier;
        [FieldOffset(12)] public uint DivisorLog2;
        [FieldOffset(16)] public SystemClockContext Context; // 类型已为 public
    }
}
