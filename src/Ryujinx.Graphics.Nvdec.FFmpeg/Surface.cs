using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;
using System.Runtime.InteropServices;

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

        private bool _isDisposed = false;
        private bool _buffersAllocated = false;

        public Surface(int width, int height)
        {
            RequestedWidth = width;
            RequestedHeight = height;

            // 分配 AVFrame
            Frame = FFmpegApi.av_frame_alloc();
            
            if (Frame == null)
            {
                throw new OutOfMemoryException("Failed to allocate AVFrame");
            }

            // 初始化 AVFrame 的基本参数
            Frame->Width = width;
            Frame->Height = height;
            Frame->Format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

            // 立即分配缓冲区
            if (!EnsureBuffersAllocated())
            {
                throw new InvalidOperationException("Failed to allocate surface buffers");
            }

            Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                $"Surface allocated: {width}x{height}, Frame: 0x{(ulong)Frame:X16}, Buffers: {_buffersAllocated}");
        }

        public bool IsValid()
        {
            if (_isDisposed || Frame == null)
                return false;

            if (Frame->Data[0] == null || Frame->Data[1] == null || Frame->Data[2] == null)
            {
                return false;
            }

            if (Frame->LineSize[0] <= 0 || Frame->LineSize[1] <= 0 || Frame->LineSize[2] <= 0)
            {
                return false;
            }

            return _buffersAllocated;
        }

        public string GetDebugInfo()
        {
            if (_isDisposed)
                return "Surface is disposed";

            if (Frame == null)
                return "Frame is null";

            return $"Surface: {Width}x{Height}, " +
                   $"Format: {Frame->Format}, " +
                   $"BuffersAllocated: {_buffersAllocated}, " +
                   $"Data[0]: 0x{(ulong)Frame->Data[0]:X16}, " +
                   $"Data[1]: 0x{(ulong)Frame->Data[1]:X16}, " +
                   $"Data[2]: 0x{(ulong)Frame->Data[2]:X16}, " +
                   $"LineSize: [{Frame->LineSize[0]}, {Frame->LineSize[1]}, {Frame->LineSize[2]}]";
        }

        public bool EnsureBuffersAllocated()
        {
            if (_isDisposed || Frame == null)
                return false;

            if (_buffersAllocated)
                return true;

            try
            {
                int result = FFmpegApi.av_frame_get_buffer(Frame, 32);
                if (result < 0)
                {
                    Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                        $"Failed to allocate frame buffers: {result}");
                    return false;
                }

                _buffersAllocated = true;

                if (Frame->Data[0] == null || Frame->Data[1] == null || Frame->Data[2] == null)
                {
                    Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                        "Frame buffers allocated but data pointers are still null");
                    _buffersAllocated = false;
                    return false;
                }

                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Frame buffers allocated successfully");

                return true;
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error allocating frame buffers: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 填充诊断图案 - 更清晰的测试图案
        /// </summary>
        public void FillDiagnosticPattern()
        {
            if (!IsValid())
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Cannot fill diagnostic pattern - surface is invalid");
                return;
            }

            try
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Filling diagnostic pattern");

                byte* yPlane = (byte*)Frame->Data[0];
                byte* uPlane = (byte*)Frame->Data[1];
                byte* vPlane = (byte*)Frame->Data[2];
                
                int yStride = Frame->LineSize[0];
                int uvStride = Frame->LineSize[1];

                // 创建更清晰的诊断图案：左右分屏，不同灰度
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        // 左半部分：白色到灰色的渐变
                        // 右半部分：黑色到灰色的渐变
                        byte luminance;
                        if (x < Width / 2)
                        {
                            // 左半部分：从白色(255)到灰色(128)
                            luminance = (byte)(255 - (x * 127) / (Width / 2));
                        }
                        else
                        {
                            // 右半部分：从黑色(0)到灰色(128)
                            luminance = (byte)(( (x - Width / 2) * 128) / (Width / 2));
                        }
                        yPlane[y * yStride + x] = luminance;
                    }
                }

                // UV 平面填充中性色（灰色）
                for (int y = 0; y < UvHeight; y++)
                {
                    for (int x = 0; x < UvWidth; x++)
                    {
                        uPlane[y * uvStride + x] = 128;
                        vPlane[y * uvStride + x] = 128;
                    }
                }

                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Diagnostic pattern filled successfully - should show left(white-gray) right(black-gray)");
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error filling diagnostic pattern: {ex.Message}");
            }
        }

        /// <summary>
        /// 填充彩色测试图案 - 验证颜色处理
        /// </summary>
        public void FillColorTestPattern()
        {
            if (!IsValid())
                return;

            try
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Filling color test pattern");

                byte* yPlane = (byte*)Frame->Data[0];
                byte* uPlane = (byte*)Frame->Data[1];
                byte* vPlane = (byte*)Frame->Data[2];
                
                int yStride = Frame->LineSize[0];
                int uvStride = Frame->LineSize[1];

                // 创建四象限彩色测试图案
                int halfWidth = Width / 2;
                int halfHeight = Height / 2;

                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        byte luminance = 0;
                        int uvX = x / 2;
                        int uvY = y / 2;

                        if (x < halfWidth && y < halfHeight)
                        {
                            // 左上：红色
                            luminance = 76;
                            uPlane[uvY * uvStride + uvX] = 84;
                            vPlane[uvY * uvStride + uvX] = 255;
                        }
                        else if (x >= halfWidth && y < halfHeight)
                        {
                            // 右上：绿色
                            luminance = 149;
                            uPlane[uvY * uvStride + uvX] = 43;
                            vPlane[uvY * uvStride + uvX] = 21;
                        }
                        else if (x < halfWidth && y >= halfHeight)
                        {
                            // 左下：蓝色
                            luminance = 29;
                            uPlane[uvY * uvStride + uvX] = 255;
                            vPlane[uvY * uvStride + uvX] = 107;
                        }
                        else
                        {
                            // 右下：白色
                            luminance = 255;
                            uPlane[uvY * uvStride + uvX] = 128;
                            vPlane[uvY * uvStride + uvX] = 128;
                        }

                        yPlane[y * yStride + x] = luminance;
                    }
                }

                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Color test pattern filled - should show four color quadrants");
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error filling color test pattern: {ex.Message}");
            }
        }

        public void FillTestPattern()
        {
            FillDiagnosticPattern(); // 默认使用诊断图案
        }

        public bool CopyFromAVFrame(AVFrame* sourceFrame)
        {
            if (_isDisposed || Frame == null || sourceFrame == null)
                return false;

            try
            {
                if (!EnsureBuffersAllocated())
                    return false;

                int result = FFmpegApi.av_frame_copy(Frame, sourceFrame);
                if (result < 0)
                {
                    Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                        $"Failed to copy frame: {result}");
                    return false;
                }

                FFmpegApi.av_frame_copy_props(Frame, sourceFrame);

                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Frame copied: {Width}x{Height}");

                return true;
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error copying frame: {ex.Message}");
                return false;
            }
        }

        public void Clear()
        {
            if (!IsValid())
                return;

            try
            {
                byte* yPlane = (byte*)Frame->Data[0];
                byte* uPlane = (byte*)Frame->Data[1];
                byte* vPlane = (byte*)Frame->Data[2];
                
                int yStride = Frame->LineSize[0];
                int uvStride = Frame->LineSize[1];

                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        yPlane[y * yStride + x] = 0;
                    }
                }

                for (int y = 0; y < UvHeight; y++)
                {
                    for (int x = 0; x < UvWidth; x++)
                    {
                        uPlane[y * uvStride + x] = 128;
                        vPlane[y * uvStride + x] = 128;
                    }
                }

                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Surface cleared to black");
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error clearing surface: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Disposing Surface: 0x{(ulong)Frame:X16}");

                if (Frame != null)
                {
                    FFmpegApi.av_frame_unref(Frame);
                    AVFrame* framePtr = Frame;
                    FFmpegApi.av_frame_free(&framePtr);
                }

                _isDisposed = true;
                _buffersAllocated = false;
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error disposing Surface: {ex.Message}");
            }
        }

        ~Surface()
        {
            if (!_isDisposed)
            {
                Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Surface was not disposed properly!");
                Dispose();
            }
        }
    }
}
