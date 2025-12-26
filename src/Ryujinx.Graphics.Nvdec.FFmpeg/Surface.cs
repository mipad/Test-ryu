using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class Surface : ISurface
    {
        public AVFrame* Frame { get; }
        public AVFrame* HardwareFrame { get; private set; }

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

        public bool IsHardwareFrame => Frame->format == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            Frame = FFmpegApi.av_frame_alloc();
            if (Frame == null)
            {
                throw new OutOfMemoryException("Failed to allocate AVFrame");
            }
        }

        // 为硬件帧分配软件帧副本
        public AVFrame* CreateSoftwareFrameCopy()
        {
            if (HardwareFrame != null)
            {
                FFmpegApi.av_frame_unref(HardwareFrame);
                FFmpegApi.av_free(HardwareFrame);
            }

            HardwareFrame = FFmpegApi.av_frame_alloc();
            
            // 从硬件帧复制数据
            if (IsHardwareFrame)
            {
                int result = FFmpegApi.av_hwframe_transfer_data(HardwareFrame, Frame, 0);
                if (result < 0)
                {
                    FFmpegApi.av_frame_free(&HardwareFrame);
                    return null;
                }
            }
            else
            {
                // 如果是软件帧，直接复制
                FFmpegApi.av_frame_ref(HardwareFrame, Frame);
            }

            return HardwareFrame;
        }

        // 获取用于显示的帧（如果是硬件帧则返回转换后的软件帧）
        public AVFrame* GetDisplayFrame()
        {
            if (IsHardwareFrame)
            {
                if (HardwareFrame == null)
                {
                    CreateSoftwareFrameCopy();
                }
                return HardwareFrame ?? Frame;
            }
            
            return Frame;
        }

        // 获取平面数据，处理硬件帧
        public IntPtr GetPlanePointer(int plane)
        {
            if (IsHardwareFrame && HardwareFrame != null)
            {
                return (IntPtr)HardwareFrame->Data[plane];
            }
            
            return (IntPtr)Frame->Data[plane];
        }

        // 获取步幅，处理硬件帧
        public int GetPlaneStride(int plane)
        {
            if (IsHardwareFrame && HardwareFrame != null)
            {
                return HardwareFrame->LineSize[plane];
            }
            
            return Frame->LineSize[plane];
        }

        public void Dispose()
        {
            if (Frame != null)
            {
                FFmpegApi.av_frame_unref(Frame);
                FFmpegApi.av_free(Frame);
            }

            if (HardwareFrame != null)
            {
                FFmpegApi.av_frame_unref(HardwareFrame);
                FFmpegApi.av_free(HardwareFrame);
            }
        }
    }
}
