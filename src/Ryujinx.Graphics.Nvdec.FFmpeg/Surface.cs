using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class Surface : ISurface
    {
        public AVFrame* Frame { get; }
        public AVFrame* SwFrame { get; private set; } // 软件帧用于格式转换

        public int RequestedWidth { get; }
        public int RequestedHeight { get; }
        
        // 使用正确的 AVFrame.Data 访问方式
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

        // 添加像素格式属性
        public FFmpegApi.AVPixelFormat PixelFormat => (FFmpegApi.AVPixelFormat)Frame->Format;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            Frame = FFmpegApi.av_frame_alloc();
            SwFrame = null;
            
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

        // 为软件帧分配缓冲区
        public bool AllocateSwFrame()
        {
            if (SwFrame == null)
            {
                SwFrame = FFmpegApi.av_frame_alloc();
                if (SwFrame == null)
                    return false;
                
                SwFrame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                SwFrame->Width = Frame->Width;
                SwFrame->Height = Frame->Height;
                
                return FFmpegApi.av_frame_get_buffer(SwFrame, 32) >= 0;
            }
            return true;
        }

        // 从硬件帧转换到软件帧
        public bool TransferFromHardwareFrame(AVFrame* hwFrame)
        {
            if (SwFrame == null && !AllocateSwFrame())
                return false;
            
            FFmpegApi.av_frame_unref(SwFrame);
            
            int result = FFmpegApi.av_hwframe_transfer_data(SwFrame, hwFrame, 0);
            if (result < 0)
                return false;
            
            // 复制数据到主帧
            FFmpegApi.av_frame_unref(Frame);
            
            // 复制帧属性
            Frame->Width = SwFrame->Width;
            Frame->Height = SwFrame->Height;
            Frame->Format = SwFrame->Format;
            Frame->Pts = SwFrame->Pts;
            
            // 复制数据指针和行大小
            for (int i = 0; i < 8; i++)
            {
                Frame->Data[i] = SwFrame->Data[i];
                Frame->LineSize[i] = SwFrame->LineSize[i];
            }
            
            return true;
        }

        public void Dispose()
        {
            if (SwFrame != null)
            {
                FFmpegApi.av_frame_unref(SwFrame);
                FFmpegApi.av_free(SwFrame);
            }
            
            FFmpegApi.av_frame_unref(Frame);
            FFmpegApi.av_free(Frame);
        }
    }
}