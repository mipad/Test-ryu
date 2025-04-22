using System;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Time.Types
{
    // 使用 StructLayout 强制按顺序排列字段，并设置 4 字节对齐（适合 Android ARM 架构）
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SteadyClockContext
    {
        public ulong InternalOffset;     // 占用 0~7 字节（8 字节）
        public UInt128 ClockSourceId;    // 占用 8~23 字节（16 字节，对齐到 8 字节）
    }
}
