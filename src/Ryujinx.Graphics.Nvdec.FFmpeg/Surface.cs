using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class Surface : ISurface
    {
        public AVFrame* Frame { get; }
        public AVFrame* SwFrame { get; private set; }

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
        
        public bool AllocateBuffer()
        {
            if (FFmpegApi.av_frame_get_buffer(Frame, 32) >= 0)
            {
                return true;
            }
            return false;
        }

        public bool AllocateSwFrame()
        {
            if (SwFrame == null)
            {
                SwFrame = FFmpegApi.av_frame_alloc();
                if (SwFrame == null)
                    return false;
                
                SwFrame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                return true;
            }
            return true;
        }

        // 修改此方法，使其能正确处理任何帧格式（包括硬件和软件帧）
        public bool TransferFromHardwareFrame(AVFrame* inputFrame)
        {
            try
            {
                // 清空当前帧
                FFmpegApi.av_frame_unref(Frame);
                
                // 复制帧属性
                Frame->Width = inputFrame->Width;
                Frame->Height = inputFrame->Height;
                Frame->Format = inputFrame->Format;
                Frame->Pts = inputFrame->Pts;
                
                // 如果输入帧是软件帧格式，直接复制数据指针
                if (inputFrame->Format != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC &&
                    inputFrame->Format != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_NV12) // 添加更多硬件格式检查
                {
                    // 软件帧：分配缓冲区并复制数据
                    if (FFmpegApi.av_frame_get_buffer(Frame, 32) < 0)
                        return false;
                    
                    // 复制数据
                    for (int i = 0; i < 4; i++)
                    {
                        if (inputFrame->Data[i] != null && Frame->Data[i] != null)
                        {
                            int height = (i == 0) ? Frame->Height : (Frame->Height + 1) / 2;
                            int linesize = Math.Min(inputFrame->LineSize[i], Frame->LineSize[i]);
                            
                            for (int j = 0; j < height; j++)
                            {
                                byte* src = inputFrame->Data[i] + j * inputFrame->LineSize[i];
                                byte* dst = Frame->Data[i] + j * Frame->LineSize[i];
                                Unsafe.CopyBlock(dst, src, (uint)linesize);
                            }
                        }
                    }
                    return true;
                }
                else
                {
                    // 硬件帧：需要先传输到软件帧再复制
                    if (!AllocateSwFrame())
                        return false;
                    
                    FFmpegApi.av_frame_unref(SwFrame);
                    
                    int result = FFmpegApi.av_hwframe_transfer_data(SwFrame, inputFrame, 0);
                    if (result < 0)
                        return false;
                    
                    // 递归调用处理软件帧
                    return TransferFromHardwareFrame(SwFrame);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (SwFrame != null)
            {
                FFmpegApi.av_frame_unref(SwFrame);
                FFmpegApi.av_free(SwFrame);
                SwFrame = null;
            }
            
            FFmpegApi.av_frame_unref(Frame);
            FFmpegApi.av_free(Frame);
        }
    }
}
