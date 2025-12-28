using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    // 硬件解码相关的新结构体
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVBufferRef
    {
        public unsafe AVBuffer* Buffer;
        public unsafe byte* Data;
        public int Size;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVBuffer
    {
        // AVBuffer的内部结构，通常不需要直接访问
    }
    
    [StructLayout(LayoutKind.Sequential)]
    internal struct AVCodecHWConfig
    {
        public FFmpegApi.AVPixelFormat PixFmt;
        public FFmpegApi.AVHWDeviceType DeviceType;
        public int Methods;
        public int DeviceCaps;
        public unsafe byte* ConstraintSets;
        public int NumConstraintSets;
    }
}
