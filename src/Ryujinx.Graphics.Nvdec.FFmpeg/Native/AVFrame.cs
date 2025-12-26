using Ryujinx.Common.Memory;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct AVFrame
    {
        public Array8<IntPtr> Data;
        public Array8<int> LineSize;
        public IntPtr ExtendedData;
        public int Width;
        public int Height;
        public int NumSamples;
        public int Format;
        public int KeyFrame;
        public int PictureType;
        public AVRational SampleAspectRatio;
        public long Pts;
        public long PktDts;
        public AVRational TimeBase;
        public int CodedPictureNumber;
        public int DisplayPictureNumber;
        public int Quality;
        public IntPtr Opaque;
        public int RepeatPicture;
        public int InterlacedFrame;
        public int TopFieldFirst;
        public int PaletteHasChanged;
        public long ReorderedOpaque;
        public int SampleRate;
        public ulong ChannelLayout;
        
        // 硬件帧上下文（新增）
        public IntPtr hw_frames_ctx;
        
        // 帧标志（新增）
        public int flags;
        
        // 颜色空间信息（新增）
        public int colorspace;
        public int color_range;
        
        // 裁剪信息（新增）
        public int crop_left;
        public int crop_top;
        public int crop_right;
        public int crop_bottom;
        
        // 私有数据（新增）
        public IntPtr private_ref;
        
        // NOTE: 这个结构对应 FFmpeg 的 AVFrame，保持与 FFmpeg 头文件兼容
    }
}
