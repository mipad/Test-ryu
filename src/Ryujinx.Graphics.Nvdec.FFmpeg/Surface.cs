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

        // 添加调试信息
        private bool _isDisposed = false;
        private readonly string _creationStackTrace;

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
            Frame->Format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P; // 明确设置为 YUV420P

            // 记录创建堆栈用于调试
            #if DEBUG
            _creationStackTrace = Environment.StackTrace;
            #endif

            // 调试日志
            Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                $"Surface allocated: {width}x{height}, Frame: 0x{(ulong)Frame:X16}");
        }

        /// <summary>
        /// 检查 Surface 是否有效
        /// </summary>
        public bool IsValid()
        {
            if (_isDisposed || Frame == null)
                return false;

            // 检查关键数据指针
            if (Frame->Data[0] == null || Frame->Data[1] == null || Frame->Data[2] == null)
                return false;

            // 检查步长
            if (Frame->LineSize[0] <= 0 || Frame->LineSize[1] <= 0 || Frame->LineSize[2] <= 0)
                return false;

            return true;
        }

        /// <summary>
        /// 获取调试信息
        /// </summary>
        public string GetDebugInfo()
        {
            if (_isDisposed)
                return "Surface is disposed";

            if (Frame == null)
                return "Frame is null";

            return $"Surface: {Width}x{Height}, " +
                   $"Format: {Frame->Format}, " +
                   $"Data[0]: 0x{(ulong)Frame->Data[0]:X16}, " +
                   $"Data[1]: 0x{(ulong)Frame->Data[1]:X16}, " +
                   $"Data[2]: 0x{(ulong)Frame->Data[2]:X16}, " +
                   $"LineSize: [{Frame->LineSize[0]}, {Frame->LineSize[1]}, {Frame->LineSize[2]}]";
        }

        /// <summary>
        /// 确保 Surface 已分配缓冲区
        /// </summary>
        public bool EnsureBuffersAllocated()
        {
            if (_isDisposed || Frame == null)
                return false;

            try
            {
                // 如果数据指针为空，分配缓冲区
                if (Frame->Data[0] == null)
                {
                    int result = FFmpegApi.av_frame_get_buffer(Frame, 32); // 32 字节对齐
                    if (result < 0)
                    {
                        Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                            $"Failed to allocate frame buffers: {result}");
                        return false;
                    }

                    Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                        "Frame buffers allocated successfully");
                }

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
        /// 填充测试数据（用于调试绿色屏幕问题）
        /// </summary>
        public void FillTestPattern()
        {
            if (!IsValid())
                return;

            try
            {
                // Y 平面（亮度） - 填充灰度渐变
                byte* yPlane = Frame->Data[0];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        byte luminance = (byte)((x * 255) / Math.Max(1, Width - 1));
                        yPlane[y * Frame->LineSize[0] + x] = luminance;
                    }
                }

                // U 和 V 平面（色度） - 填充中性灰色（无颜色）
                byte* uPlane = Frame->Data[1];
                byte* vPlane = Frame->Data[2];
                for (int y = 0; y < UvHeight; y++)
                {
                    for (int x = 0; x < UvWidth; x++)
                    {
                        uPlane[y * Frame->LineSize[1] + x] = 128; // 中性 U
                        vPlane[y * Frame->LineSize[2] + x] = 128; // 中性 V
                    }
                }

                Ryujinx.Common.Logging.Logger.Debug?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    "Test pattern filled");
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error filling test pattern: {ex.Message}");
            }
        }

        /// <summary>
        /// 从另一个 AVFrame 复制数据
        /// </summary>
        public bool CopyFromAVFrame(AVFrame* sourceFrame)
        {
            if (_isDisposed || Frame == null || sourceFrame == null)
                return false;

            try
            {
                // 首先确保我们的缓冲区已分配
                if (!EnsureBuffersAllocated())
                    return false;

                // 复制帧数据
                int result = FFmpegApi.av_frame_copy(Frame, sourceFrame);
                if (result < 0)
                {
                    Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                        $"Failed to copy frame: {result}");
                    return false;
                }

                // 复制帧属性
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

        /// <summary>
        /// 清除帧数据（填充黑色）
        /// </summary>
        public void Clear()
        {
            if (!IsValid())
                return;

            try
            {
                // Y 平面填充黑色
                byte* yPlane = Frame->Data[0];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        yPlane[y * Frame->LineSize[0] + x] = 0; // 黑色
                    }
                }

                // U 和 V 平面填充中性色
                byte* uPlane = Frame->Data[1];
                byte* vPlane = Frame->Data[2];
                for (int y = 0; y < UvHeight; y++)
                {
                    for (int x = 0; x < UvWidth; x++)
                    {
                        uPlane[y * Frame->LineSize[1] + x] = 128;
                        vPlane[y * Frame->LineSize[2] + x] = 128;
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
                    // 首先取消引用任何缓冲区
                    FFmpegApi.av_frame_unref(Frame);
                    
                    // 然后释放帧本身
                    FFmpegApi.av_frame_free(&Frame);
                }

                _isDisposed = true;
            }
            catch (Exception ex)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.FFmpeg, 
                    $"Error disposing Surface: {ex.Message}");
            }
        }

        /// <summary>
        /// 析构函数 - 安全网
        /// </summary>
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
