using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Time.Clock.Types
{
    // 显式指定字段偏移和结构体总大小
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct ContinuousAdjustmentTimePoint
    {
        [FieldOffset(0)]  public ulong ClockOffset;    // 0~7 字节
        [FieldOffset(8)]  public uint Multiplier;      // 8~11 字节（从 long 改为 uint）
        [FieldOffset(12)] public uint DivisorLog2;     // 12~15 字节（从 long 改为 uint）
        [FieldOffset(16)] public SystemClockContext Context; // 16~31 字节（假设 SystemClockContext 占用 16 字节）
    }
}
