using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class Surface : ISurface
    {
        public AVFrame* Frame { get; }

        public int RequestedWidth { get; }
        public int RequestedHeight { get; }

        public Plane YPlane => new((IntPtr)Frame->Data[0], Stride * Height);
        public Plane UPlane => new((IntPtr)Frame->Data[1], UvStride * UvHeight);
        public Plane VPlane => new((IntPtr)Frame->Data[2], UvStride * UvHeight);

        public FrameField Field => Frame->InterlacedFrame != 0 ? FrameField.Interlaced : FrameField.Progressive;

        public int Width => Frame->Width;
        public int Height => Frame->Height;
        public int Stride => Frame->LineSize[0];
        public int UvWidth => (Width + 1) >> 1;
        public int UvHeight => (Height + 1) >> 1;
        public int UvStride => Frame->LineSize[1];

        // 新增属性：帧格式
        public AVPixelFormat Format => (AVPixelFormat)Frame->Format;

        // 新增属性：检查是否为硬件帧
        public bool IsHardwareFrame => Format == AVPixelFormat.AV_PIX_FMT_MEDIACODEC;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            Frame = FFmpegApi.av_frame_alloc();
        }

        public void Dispose()
        {
            FFmpegApi.av_frame_unref(Frame);
            FFmpegApi.av_free(Frame);
        }

        // 新增方法：获取帧信息用于调试
        public string GetFrameInfo()
        {
            return $"Format: {Format}, Width: {Width}, Height: {Height}, Stride: {Stride}, Linesize: [{Frame->LineSize[0]}, {Frame->LineSize[1]}, {Frame->LineSize[2]}]";
        }
    }
}