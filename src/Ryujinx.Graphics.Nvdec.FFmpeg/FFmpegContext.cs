using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);

        private AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private AVCodecContext* _context;
        private readonly bool _useNewApi;
        private bool _isFirstFrame = true;
        private bool _needsFlush = false;
        private System.Diagnostics.Stopwatch _decodeTimer = new System.Diagnostics.Stopwatch();
        private int _frameCount = 0;
        private readonly bool _useHardwareDecoder;
        private readonly string _decoderType;
        private readonly string _hardwareDecoderName;
        private AVBufferRef* _hwDeviceContext;
        private AVPixelFormat _hwPixelFormat;
        private bool _isInitialized = false;

        // get_format 回调委托
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate AVPixelFormat GetFormatDelegate(AVCodecContext* s, AVPixelFormat* pix_fmts);

        private GetFormatDelegate _getFormatCallback;
        private IntPtr _getFormatCallbackPtr;

        // Android 硬件解码器映射
        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_HEVC, new[] { "hevc_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP9, new[] { "vp9_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_AV1, new[] { "av1_mediacodec" } },
            
        };

        public FFmpegContext(AVCodecID codecId, bool preferHardware = true)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"Initializing FFmpeg decoder for {codecId}, Hardware preference: {preferHardware}");

            // 设置 get_format 回调
            _getFormatCallback = GetHwFormat;
            _getFormatCallbackPtr = Marshal.GetFunctionPointerForDelegate(_getFormatCallback);

            // 直接初始化只读字段，而不是通过方法
            string hardwareDecoderName = null;
            bool useHardwareDecoder = false;
            AVCodec* codec = null;

            // 尝试硬件解码器（如果启用且可用）
            if (preferHardware)
            {
                if (AndroidHardwareDecoders.TryGetValue(codecId, out string[] decoderNames))
                {
                    foreach (string decoderName in decoderNames)
                    {
                        codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                        if (codec != null)
                        {
                            hardwareDecoderName = decoderName;
                            useHardwareDecoder = true;
                            Logger.Debug?.Print(LogClass.FFmpeg, $"Found hardware decoder: {decoderName}");
                            break;
                        }
                        else
                        {
                            Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder not available: {decoderName}");
                        }
                    }
                }

                if (codec != null)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected hardware decoder: {hardwareDecoderName}");
                }
                else
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"No compatible hardware decoder found for {codecId}");
                }
            }

            // 如果硬件解码器不可用，回退到软件解码器
            if (codec == null)
            {
                codec = FFmpegApi.avcodec_find_decoder(codecId);
                useHardwareDecoder = false;
                
                if (codec != null)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected software decoder: {GetCodecName(codec)}");
                }
            }

            if (codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found for {codecId}. Make sure you have the required codec present in your FFmpeg installation.");
                return;
            }

            // 设置只读字段
            _codec = codec;
            _useHardwareDecoder = useHardwareDecoder;
            _decoderType = useHardwareDecoder ? "Hardware" : "Software";
            _hardwareDecoderName = hardwareDecoderName;

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 设置解码器参数
            if (!ConfigureDecoderContext())
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to configure decoder context.");
                return;
            }

            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            if (openResult != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened (Error: {openResult}).");
                return;
            }

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            // 检测 API 版本
            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}, using {(avCodecMajorVersion >= 58 ? "new" : "old")} API");

            // 检测是否使用新版 API (avcodec_send_packet/avcodec_receive_frame)
            _useNewApi = avCodecMajorVersion >= 58;

            if (!_useNewApi)
            {
                // 旧版 API 路径 - 直接在构造函数中设置 _decodeFrame
                // libavcodec 59.24 changed AvCodec to move its private API
                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                }
                // libavcodec 59.x changed AvCodec private API layout.
                else if (avCodecMajorVersion == 59)
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                }
                // libavcodec 58.x and lower
                else
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                }
            }

            _isInitialized = true;
            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg {_decoderType} decoder initialized successfully (API: {(_useNewApi ? "New" : "Old")}, Codec: {GetCodecName(_codec)})");
        }

        private bool ConfigureDecoderContext()
        {
            // 基本解码器配置
            _context->ErrRecognition = 0x0001 | 0x0002 | 0x0004; // 多种错误识别标志
            _context->ErrorConcealment = 0x0001 | 0x0002; // 帧和边界错误隐藏
            _context->WorkaroundBugs = 1; // 启用bug规避

            if (_useHardwareDecoder)
            {
                try
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "Configuring hardware decoder...");

                    // 创建硬件设备上下文
                    AVHWDeviceType deviceType = FFmpegApi.av_hwdevice_find_type_by_name("mediacodec");
                    if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, "MediaCodec hardware device type not supported");
                        return false;
                    }

                    Logger.Debug?.Print(LogClass.FFmpeg, $"Found MediaCodec device type: {deviceType}");

                    // 创建硬件设备上下文
                    int result;
                    fixed (AVBufferRef** ppHwDeviceContext = &_hwDeviceContext)
                    {
                        result = FFmpegApi.av_hwdevice_ctx_create(ppHwDeviceContext, deviceType, null, null, 0);
                    }
                    
                    if (result < 0)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to create hardware device context: {result}");
                        return false;
                    }

                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware device context created successfully");

                    // 获取硬件解码器配置 - 参考案例中的方法
                    _hwPixelFormat = FindHardwarePixelFormat(_codec, deviceType);
                    if (_hwPixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, "Failed to find compatible hardware pixel format");
                        return false;
                    }

                    Logger.Debug?.Print(LogClass.FFmpeg, $"Selected hardware pixel format: {_hwPixelFormat}");

                    // 设置硬件设备上下文到编解码器上下文
                    _context->HwDeviceCtx = (nint)FFmpegApi.av_buffer_ref(_hwDeviceContext);
                    if (_context->HwDeviceCtx == IntPtr.Zero)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, "Failed to set hardware device context");
                        return false;
                    }

                    // 设置 get_format 回调 - 关键修复
                    _context->GetFormat = _getFormatCallbackPtr;

                    // 硬件解码器优化设置
                    _context->Flags2 |= 0x00000001; // CODEC_FLAG2_FAST - 快速解码
                    _context->ThreadCount = 1; // 硬件解码器通常单线程
                    
                    // 减少错误恢复，硬件解码器通常更稳定
                    _context->ErrRecognition = 0x0001;
                    
                    Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder configured successfully");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"Exception configuring hardware decoder: {ex.Message}");
                    return false;
                }
            }
            else
            {
                // 软件解码器优化
                _context->ThreadCount = Math.Min(Environment.ProcessorCount, 4);
                _context->Refs = 3; // 限制参考帧数量
                
                Logger.Debug?.Print(LogClass.FFmpeg, $"Configured for software decoding ({_context->ThreadCount} threads)");
                return true;
            }
        }

        // 参考案例的硬件配置查找方法
        private AVPixelFormat FindHardwarePixelFormat(AVCodec* codec, AVHWDeviceType deviceType)
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "Finding hardware pixel format using case reference method...");

            // 遍历硬件配置，查找匹配的设备类型
            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(codec, i);
                if (config == null)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"No more hardware configs found at index {i}");
                    break;
                }

                Logger.Debug?.Print(LogClass.FFmpeg, $"Checking hardware config {i}: methods={config->methods}, device_type={config->device_type}, pix_fmt={config->pix_fmt}");

                // 检查配置方法是否包含硬件设备上下文，并且设备类型匹配 - 参考案例中的条件
                if ((config->methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                    config->device_type == deviceType)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Found compatible hardware config: pix_fmt={config->pix_fmt}");
                    return (AVPixelFormat)config->pix_fmt;
                }
            }

            Logger.Warning?.Print(LogClass.FFmpeg, "No compatible hardware configuration found");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        // get_format 回调函数 - 关键修复，参考案例
        private AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pix_fmts)
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "get_format callback called");

            // 遍历支持的像素格式列表
            for (AVPixelFormat* p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                Logger.Debug?.Print(LogClass.FFmpeg, $"Checking pixel format in callback: {*p}");
                if (*p == _hwPixelFormat)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected hardware pixel format in callback: {*p}");
                    return *p;
                }
            }

            Logger.Error?.Print(LogClass.FFmpeg, "Failed to get HW surface format in callback");
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private AVPixelFormat GetHardwarePixelFormat(AVCodec* codec, AVHWDeviceType deviceType)
        {
            // 遍历硬件配置，查找匹配的设备类型
            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(codec, i);
                if (config == null)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"No hardware config found for decoder at index {i}");
                    break;
                }

                Logger.Debug?.Print(LogClass.FFmpeg, $"Checking hardware config {i}: methods={config->methods}, device_type={config->device_type}, pix_fmt={config->pix_fmt}");

                // 检查配置方法是否包含硬件设备上下文，并且设备类型匹配
                if ((config->methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                    config->device_type == deviceType)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"Found compatible hardware config: pix_fmt={config->pix_fmt}");
                    return (AVPixelFormat)config->pix_fmt;
                }
            }

            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private string GetCodecName(AVCodec* codec)
        {
            if (codec == null) return "Unknown";
            return Marshal.PtrToStringAnsi((IntPtr)codec->Name) ?? "Unknown";
        }

        static FFmpegContext()
        {
            _logFunc = Log;

            // Redirect log output.
            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(_logFunc);
        }

        private static void Log(void* ptr, AVLog level, string format, byte* vl)
        {
            if (level > FFmpegApi.av_log_get_level())
            {
                return;
            }

            int lineSize = 1024;
            byte* lineBuffer = stackalloc byte[lineSize];
            int printPrefix = 1;

            FFmpegApi.av_log_format_line(ptr, level, format, vl, lineBuffer, lineSize, &printPrefix);

            string line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer)?.Trim() ?? string.Empty;

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
            // 检查是否已初始化
            if (!_isInitialized || _context == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Decoder not initialized, cannot decode frame");
                return -1;
            }

            _decodeTimer.Start();
            
            FFmpegApi.av_frame_unref(output.Frame);

            // 如果需要刷新，先刷新解码器
            if (_needsFlush)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
                _needsFlush = false;
                Logger.Debug?.Print(LogClass.FFmpeg, "Flushed decoder buffers due to previous errors");
            }

            int result;
            if (_useNewApi)
            {
                // 使用新版 API: avcodec_send_packet/avcodec_receive_frame
                result = DecodeFrameNewApi(output, bitstream);
            }
            else
            {
                // 使用旧版 API
                result = DecodeFrameOldApi(output, bitstream);
            }
            
            _decodeTimer.Stop();
            _frameCount++;

            // 性能监控
            if (_frameCount % 100 == 0)
            {
                double totalTime = _decodeTimer.Elapsed.TotalMilliseconds;
                double avgTime = totalTime / _frameCount;
                double fps = 1000.0 / avgTime;
                
                Logger.Info?.Print(LogClass.FFmpeg, 
                    $"{_decoderType} decode stats: {_frameCount} frames, {avgTime:F2}ms/frame, {fps:F1} FPS, Total: {totalTime:F0}ms");
            }

            return result;
        }

        private int DecodeFrameNewApi(Surface output, ReadOnlySpan<byte> bitstream)
        {
            int result;
            int gotFrame = 0;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                
                // 发送 packet 到解码器
                result = FFmpegApi.avcodec_send_packet(_context, _packet);
                if (result < 0 && result != FFmpegApi.EAGAIN && result != FFmpegApi.EOF)
                {
                    LogDecodeError("avcodec_send_packet", result);
                    _needsFlush = true;
                    FFmpegApi.av_packet_unref(_packet);
                    return -1;
                }

                // 接收解码后的 frame
                result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                if (result >= 0)
                {
                    gotFrame = 1;
                    _isFirstFrame = false;

                    // 如果是硬件解码，可能需要转换帧格式
                    if (_useHardwareDecoder && output.Frame->Format == (int)_hwPixelFormat)
                    {
                        Logger.Debug?.Print(LogClass.FFmpeg, "Hardware frame decoded, may need format conversion");
                    }
                }
                else if (result == FFmpegApi.EAGAIN || result == FFmpegApi.EOF)
                {
                    // 需要更多输入数据或到达流结尾
                    gotFrame = 0;
                    result = 0;
                    _isFirstFrame = false;
                }
                else
                {
                    LogDecodeError("avcodec_receive_frame", result);
                    _needsFlush = true;
                    gotFrame = 0;
                }
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }

            return result < 0 ? result : 0;
        }

        private int DecodeFrameOldApi(Surface output, ReadOnlySpan<byte> bitstream)
        {
            // 安全检查
            if (_context == null || output.Frame == null || _decodeFrame == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Invalid state in DecodeFrameOldApi");
                return -1;
            }

            int result;
            int gotFrame;

            // 创建临时数据包用于解码
            AVPacket* tempPacket = FFmpegApi.av_packet_alloc();
            if (tempPacket == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Failed to allocate temporary packet");
                return -1;
            }

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    tempPacket->Data = ptr;
                    tempPacket->Size = bitstream.Length;
                    result = _decodeFrame(_context, output.Frame, &gotFrame, tempPacket);
                }

                if (result < 0)
                {
                    LogDecodeError("DecodeFrame", result);
                    
                    if (_isFirstFrame || result == -1094995529) // AVERROR_INVALIDDATA
                    {
                        _needsFlush = true;
                        _isFirstFrame = false;
                        FFmpegApi.av_frame_unref(output.Frame);
                        return -1;
                    }
                }

                if (gotFrame == 0)
                {
                    FFmpegApi.av_frame_unref(output.Frame);

                    // 尝试获取延迟帧
                    tempPacket->Data = null;
                    tempPacket->Size = 0;
                    result = _decodeFrame(_context, output.Frame, &gotFrame, tempPacket);
                    _context->HasBFrames = 0; // 重置 B 帧计数
                }

                if (gotFrame == 0)
                {
                    FFmpegApi.av_frame_unref(output.Frame);
                    return -1;
                }

                _isFirstFrame = false;
                return result < 0 ? result : 0;
            }
            finally
            {
                FFmpegApi.av_packet_unref(tempPacket);
                FFmpegApi.av_packet_free(&tempPacket);
            }
        }

        private void LogDecodeError(string operation, int errorCode)
        {
            string errorType = _useHardwareDecoder ? "hardware" : "software";
            Logger.Warning?.Print(LogClass.FFmpeg, $"{operation} failed with {errorType} decoder (Error: {errorCode})");
            
            if (_useHardwareDecoder && errorCode == -1313558101) // AVERROR_UNKNOWN
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decoder specific error, consider using software fallback");
            }
        }

        // 公开属性用于查询解码器状态
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string DecoderType => _decoderType;
        public string CodecName => GetCodecName(_codec);
        public string HardwareDecoderName => _hardwareDecoderName ?? "None";
        public bool IsInitialized => _isInitialized;

        public void Dispose()
        {
            // 清理数据包
            if (_packet != null)
            {
                fixed (AVPacket** ppPacket = &_packet)
                {
                    FFmpegApi.av_packet_free(ppPacket);
                }
            }

            // 清理硬件设备上下文
            if (_hwDeviceContext != null)
            {
                fixed (AVBufferRef** ppHwDeviceContext = &_hwDeviceContext)
                {
                    FFmpegApi.av_buffer_unref(ppHwDeviceContext);
                }
            }

            // 刷新解码器缓冲区
            if (_useNewApi && _context != null)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
            }

            // 清理编解码器上下文
            if (_context != null)
            {
                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }
            
            Logger.Debug?.Print(LogClass.FFmpeg, $"{_decoderType} decoder disposed");
        }

        // 新增方法：检查硬件解码器支持状态
        public static bool CheckHardwareDecoderSupport(AVCodecID codecId)
        {
            try
            {
                // 首先检查 MediaCodec 设备类型是否支持
                AVHWDeviceType deviceType = FFmpegApi.av_hwdevice_find_type_by_name("mediacodec");
                if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "MediaCodec hardware device type not supported");
                    return false;
                }

                if (AndroidHardwareDecoders.TryGetValue(codecId, out string[] decoderNames))
                {
                    foreach (string decoderName in decoderNames)
                    {
                        AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                        if (codec != null)
                        {
                            // 检查是否有兼容的硬件配置
                            for (int i = 0; ; i++)
                            {
                                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(codec, i);
                                if (config == null) break;
                                
                                if ((config->methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                                    config->device_type == deviceType)
                                {
                                    Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder available: {decoderName} with pix_fmt {config->pix_fmt}");
                                    return true;
                                }
                            }
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Hardware decoder check failed: {ex.Message}");
                return false;
            }
        }
    }
}
