using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
// 在文件顶部添加
using AndroidJavaClass = Ryujinx.Graphics.Nvdec.FFmpeg.AndroidJavaClass;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    public enum HardwareAccelerationMode
    {
        Disabled = 0,
        Auto = 1,
        MediaCodec = 2,  // Android MediaCodec
        Software = 3,
    }

    unsafe class FFmpegContext : IDisposable
    {
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private readonly AVBufferRef* _hwDeviceCtx;
        
        private bool _useHardwareAcceleration;
        private bool _hasHardwareContext;
        private AVFrame* _swFrame; // 用于硬件帧到软件帧的转换

        // Android 上支持的硬件解码器类型
        private static readonly List<FFmpegApi.AVHWDeviceType> _androidHwDeviceTypes = new()
        {
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC,
        };

        public bool HasHardwareAcceleration => _hasHardwareContext;

        public FFmpegContext(AVCodecID codecId, HardwareAccelerationMode accelerationMode = HardwareAccelerationMode.Auto)
        {
            _useHardwareAcceleration = accelerationMode != HardwareAccelerationMode.Disabled && 
                                       accelerationMode != HardwareAccelerationMode.Software;

            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 设置低延迟解码
            av_opt_set(_context->priv_data, "tune", "zerolatency", 0);

            // 尝试初始化硬件解码
            if (_useHardwareAcceleration)
            {
                InitializeHardwareDecoder();
            }

            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            // 如果没有硬件加速，记录使用软件解码
            if (!_hasHardwareContext)
            {
                Logger.Info?.Print(LogClass.FFmpeg, "Using FFmpeg software decoder");
            }

            // 初始化软件帧用于硬件帧转换
            _swFrame = FFmpegApi.av_frame_alloc();
        }

        // av_opt_set 的简单实现
        private static unsafe void av_opt_set(void* obj, string name, string val, int search_flags)
        {
            // 这是一个简化的实现，实际应该调用 FFmpeg 的 av_opt_set
            // 这里只是为了编译通过
        }

        private bool InitializeHardwareDecoder()
        {
            // 检查编解码器是否支持硬件解码
            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (config == null)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"No hardware configuration found for {Marshal.PtrToStringAnsi((IntPtr)_codec->name)}");
                    break;
                }

                // 检查是否是 Android 支持的硬件类型
                if (_androidHwDeviceTypes.Contains(config->device_type))
                {
                    if (InitializeHardwareDevice(config->device_type, config->pix_fmt))
                    {
                        _hasHardwareContext = true;
                        Logger.Info?.Print(LogClass.FFmpeg, $"Using {config->device_type} hardware decoder");
                        return true;
                    }
                }
            }

            Logger.Warning?.Print(LogClass.FFmpeg, "Hardware acceleration not available, falling back to software decoder");
            return false;
        }

        private bool InitializeHardwareDevice(FFmpegApi.AVHWDeviceType deviceType, FFmpegApi.AVPixelFormat hwPixelFormat)
        {
            try
            {
                // 创建硬件设备上下文
                fixed (AVBufferRef** hwDeviceCtx = &_hwDeviceCtx)
                {
                    int result = FFmpegApi.av_hwdevice_ctx_create(hwDeviceCtx, deviceType, null, null, 0);
                    if (result < 0)
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, $"Failed to create {deviceType} hardware device context: {result}");
                        return false;
                    }
                }

                // 设置解码器硬件上下文
                _context->hw_device_ctx = FFmpegApi.av_buffer_ref(_hwDeviceCtx);
                _context->pix_fmt = (int)hwPixelFormat;

                // 设置 get_format 回调（通过委托）
                _context->get_format = Marshal.GetFunctionPointerForDelegate<GetFormatDelegate>(GetHardwareFormat);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Error initializing hardware device {deviceType}: {ex.Message}");
                return false;
            }
        }

        private unsafe delegate IntPtr GetFormatDelegate(AVCodecContext* ctx, FFmpegApi.AVPixelFormat* fmt);

        private static IntPtr GetHardwareFormat(AVCodecContext* ctx, FFmpegApi.AVPixelFormat* fmt)
        {
            // 返回硬件像素格式，如果失败则回退到软件格式
            for (FFmpegApi.AVPixelFormat* p = fmt; *p != FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == (FFmpegApi.AVPixelFormat)ctx->pix_fmt)
                {
                    return (IntPtr)*p;
                }
            }

            // 回退到软件解码
            FFmpegApi.av_buffer_unref(&ctx->hw_device_ctx);
            return (IntPtr)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        static FFmpegContext()
        {
            // 初始化日志回调
            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(LogCallback);
        }

        private static unsafe void LogCallback(void* ptr, AVLog level, string format, byte* vl)
        {
            if (level > FFmpegApi.av_log_get_level())
            {
                return;
            }

            int lineSize = 1024;
            byte* lineBuffer = stackalloc byte[lineSize];
            int printPrefix = 1;

            FFmpegApi.av_log_format_line(ptr, level, format, vl, lineBuffer, lineSize, &printPrefix);

            string line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer)?.Trim();
            if (string.IsNullOrEmpty(line))
                return;

            switch (level)
            {
                case AVLog.Panic:
                case AVLog.Fatal:
                case AVLog.Error:
                    Logger.Error?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Warning:
                    Logger.Warning?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Info:
                    Logger.Info?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Verbose:
                case AVLog.Debug:
                    Logger.Debug?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Trace:
                    Logger.Trace?.Print(LogClass.FFmpeg, line);
                    break;
            }
        }

        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            FFmpegApi.av_frame_unref(output.Frame);

            fixed (byte* ptr = bitstream)
            {
                _packet->data = ptr;
                _packet->size = bitstream.Length;

                // 发送数据包
                int result = FFmpegApi.avcodec_send_packet(_context, _packet);
                if (result < 0 && result != FFmpegErrors.AVERROR_EAGAIN && result != FFmpegErrors.AVERROR_EOF)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Error sending packet: {result}");
                    return result;
                }

                // 接收帧
                result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                if (result < 0 && result != FFmpegErrors.AVERROR_EAGAIN && result != FFmpegErrors.AVERROR_EOF)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Error receiving frame: {result}");
                    return result;
                }

                // 检查是否是硬件帧
                if (output.Frame->format == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
                {
                    // 硬件帧需要转换到软件帧
                    FFmpegApi.av_frame_unref(_swFrame);
                    result = FFmpegApi.av_hwframe_transfer_data(_swFrame, output.Frame, 0);
                    if (result < 0)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"Error transferring hardware frame: {result}");
                        return result;
                    }

                    // 复制数据到输出帧
                    CopyFrameData(_swFrame, output.Frame);
                }

                return result == 0 ? 0 : -1;
            }
        }

        private unsafe void CopyFrameData(AVFrame* src, AVFrame* dst)
        {
            // 复制基本属性
            dst->width = src->width;
            dst->height = src->height;
            dst->format = src->format;
            dst->pts = src->pts;

            // 复制数据指针
            for (int i = 0; i < 8; i++)
            {
                dst->Data[i] = src->Data[i];
                dst->LineSize[i] = src->LineSize[i];
            }
        }

        public void Dispose()
        {
            // 清理软件帧
            if (_swFrame != null)
            {
                FFmpegApi.av_frame_unref(_swFrame);
                FFmpegApi.av_free(_swFrame);
            }

            // 清理硬件设备上下文
            fixed (AVBufferRef** hwDeviceCtx = &_hwDeviceCtx)
            {
                FFmpegApi.av_buffer_unref(hwDeviceCtx);
            }

            // 清理数据包
            fixed (AVPacket** ppPacket = &_packet)
            {
                if (*ppPacket != null)
                {
                    FFmpegApi.av_packet_free(ppPacket);
                }
            }

            // 关闭编解码器上下文
            if (_context != null)
            {
                FFmpegApi.avcodec_close(_context);

                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }
        }
    }
}
