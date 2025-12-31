using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Runtime.CompilerServices;
using System.Diagnostics;

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

        // 修改这里：使用 FFmpegApi.AVPixelFormat
        public FFmpegApi.AVPixelFormat PixelFormat => (FFmpegApi.AVPixelFormat)Frame->Format;

        // 记录日志
        private static readonly bool EnableDebugLogs = true;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            Frame = FFmpegApi.av_frame_alloc();
            if (Frame == null)
            {
                throw new OutOfMemoryException("Failed to allocate AVFrame");
            }
            
            SwFrame = null;
            
            // 初始化为YUV420P格式 - 使用 FFmpegApi.AVPixelFormat
            Frame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
            Frame->Width = width;
            Frame->Height = height;
            
            LogDebug($"Surface created: {width}x{height}");
        }
        
        public bool AllocateBuffer()
        {
            LogDebug("Allocating buffer for frame");
            if (FFmpegApi.av_frame_get_buffer(Frame, 32) >= 0)
            {
                LogDebug($"Buffer allocated: data[0]={Frame->Data[0]}, linesize[0]={Frame->LineSize[0]}");
                return true;
            }
            LogDebug("Failed to allocate buffer");
            return false;
        }

        public bool AllocateSwFrame()
        {
            if (SwFrame == null)
            {
                SwFrame = FFmpegApi.av_frame_alloc();
                if (SwFrame == null)
                {
                    LogDebug("Failed to allocate software frame");
                    return false;
                }
                
                SwFrame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                LogDebug("Software frame allocated");
                return true;
            }
            return true;
        }

        // 检查是否是硬件格式
        private bool IsHardwareFormat(FFmpegApi.AVPixelFormat format)
        {
            switch (format)
            {
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_VAAPI:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_D3D11:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_DXVA2_VLD:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_VDPAU:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_CUDA:
                case FFmpegApi.AVPixelFormat.AV_PIX_FMT_VULKAN:
                    return true;
                default:
                    return false;
            }
        }

        // 处理硬件帧传输
        public bool TransferFromHardwareFrame(AVFrame* inputFrame)
        {
            LogDebug($"TransferFromHardwareFrame: format={inputFrame->Format}, width={inputFrame->Width}, height={inputFrame->Height}");
            
            try
            {
                // 清空当前帧
                FFmpegApi.av_frame_unref(Frame);
                
                // 复制帧属性
                Frame->Width = inputFrame->Width;
                Frame->Height = inputFrame->Height;
                Frame->Format = inputFrame->Format;
                Frame->Pts = inputFrame->Pts;
                Frame->InterlacedFrame = inputFrame->InterlacedFrame;
                
                // 如果是软件帧格式，直接复制数据
                if (!IsHardwareFormat((FFmpegApi.AVPixelFormat)inputFrame->Format))
                {
                    LogDebug("Software frame format detected, copying directly");
                    
                    // 分配缓冲区
                    if (FFmpegApi.av_frame_get_buffer(Frame, 32) < 0)
                    {
                        LogDebug("Failed to allocate buffer for software frame");
                        return false;
                    }
                    
                    // 复制数据
                    for (int i = 0; i < 4; i++)
                    {
                        if (inputFrame->Data[i] != null && Frame->Data[i] != null)
                        {
                            int height = (i == 0) ? Frame->Height : (Frame->Height + 1) / 2;
                            int linesize = Math.Min(inputFrame->LineSize[i], Frame->LineSize[i]);
                            
                            LogDebug($"Copying plane {i}: height={height}, linesize={linesize}");
                            
                            for (int j = 0; j < height; j++)
                            {
                                byte* src = (byte*)inputFrame->Data[i] + j * inputFrame->LineSize[i];
                                byte* dst = (byte*)Frame->Data[i] + j * Frame->LineSize[i];
                                Unsafe.CopyBlock(dst, src, (uint)linesize);
                            }
                        }
                    }
                    return true;
                }
                else
                {
                    LogDebug("Hardware frame format detected, need to transfer");
                    
                    // 检查是否有硬件帧上下文
                    if (inputFrame->hw_frames_ctx == null)
                    {
                        LogDebug("Hardware frame has no hw_frames_ctx");
                        return false;
                    }
                    
                    // 分配软件帧用于接收数据
                    if (!AllocateSwFrame())
                        return false;
                    
                    // 清空软件帧
                    FFmpegApi.av_frame_unref(SwFrame);
                    
                    // 设置软件帧格式和尺寸
                    SwFrame->Width = inputFrame->Width;
                    SwFrame->Height = inputFrame->Height;
                    SwFrame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                    
                    LogDebug("Transferring hardware frame to software frame");
                    
                    // 从硬件帧传输数据到软件帧
                    int result = FFmpegApi.av_hwframe_transfer_data(SwFrame, inputFrame, 0);
                    
                    if (result < 0)
                    {
                        LogDebug($"av_hwframe_transfer_data failed: {result}");
                        return false;
                    }
                    
                    LogDebug("Hardware frame transferred successfully, copying to output frame");
                    
                    // 递归调用处理软件帧
                    return TransferFromHardwareFrame(SwFrame);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Exception in TransferFromHardwareFrame: {ex.Message}");
                return false;
            }
        }

        // 直接设置硬件帧（不复制数据）
        public bool SetHardwareFrame(AVFrame* inputFrame)
        {
            try
            {
                // 释放当前帧的资源
                FFmpegApi.av_frame_unref(Frame);
                
                // 直接复制AVFrame结构（浅复制）
                *Frame = *inputFrame;
                
                // 增加引用计数
                for (int i = 0; i < 8; i++)
                {
                    if (inputFrame->buf[i] != null)
                    {
                        Frame->buf[i] = inputFrame->buf[i];
                        // 增加引用计数
                        if (inputFrame->buf[i] != null)
                        {
                            inputFrame->buf[i]->RefCount++;
                        }
                    }
                }
                
                LogDebug($"Set hardware frame: format={Frame->Format}, width={Frame->Width}, height={Frame->Height}");
                return true;
            }
            catch (Exception ex)
            {
                LogDebug($"Exception in SetHardwareFrame: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            LogDebug("Disposing surface");
            
            if (SwFrame != null)
            {
                FFmpegApi.av_frame_unref(SwFrame);
                FFmpegApi.av_free(SwFrame);
                SwFrame = null;
            }
            
            FFmpegApi.av_frame_unref(Frame);
            FFmpegApi.av_free(Frame);
        }

        private void LogDebug(string message)
        {
            if (EnableDebugLogs)
            {
                Debug.WriteLine($"[Surface] {message}");
            }
        }
    }
}
