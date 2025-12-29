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
        
        public int PixelFormat => Frame->Format;

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
        
        // 格式检查
        public bool IsYUV420P => PixelFormat == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
        public bool IsNV12 => PixelFormat == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_NV12;
        public bool IsNV21 => PixelFormat == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_NV21;
        public bool IsMediaCodec => PixelFormat == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            Frame = FFmpegApi.av_frame_alloc();
            
            // 初始化为YUV420P格式
            Frame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
            Frame->Width = width;
            Frame->Height = height;
        }
        
        // 重新分配帧缓冲区
        public bool AllocateBuffer()
        {
            if (FFmpegApi.av_frame_get_buffer(Frame, 32) >= 0)
            {
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            FFmpegApi.av_frame_unref(Frame);
            FFmpegApi.av_free(Frame);
        }
    }
}

