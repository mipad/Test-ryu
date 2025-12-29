using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    unsafe struct AVBufferRef
    {
#pragma warning disable CS0649
        public AVBuffer* Buffer;
        public byte* Data;
        public int Size;
#pragma warning restore CS0649
    }

    struct AVBuffer
    {
        // 内部结构，通常不直接访问
    }
}