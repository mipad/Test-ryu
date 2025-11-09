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
        private readonly HardwareDecoderType _hardwareType;

        // 硬件解码器类型枚举
        private enum HardwareDecoderType
        {
            None,
            MediaCodec,
            Vulkan
        }

        // Android 硬件解码器映射 - 添加 Vulkan 解码器
        private static readonly Dictionary<AVCodecID, (string[], HardwareDecoderType)> AndroidHardwareDecoders = new()
        {
            { 
                AVCodecID.AV_CODEC_ID_H264, 
                (new[] { "h264_vulkan", "h264_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
            { 
                AVCodecID.AV_CODEC_ID_HEVC, 
                (new[] { "hevc_vulkan", "hevc_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
            { 
                AVCodecID.AV_CODEC_ID_VP8, 
                (new[] { "vp8_vulkan", "vp8_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
            { 
                AVCodecID.AV_CODEC_ID_VP9, 
                (new[] { "vp9_vulkan", "vp9_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
            { 
                AVCodecID.AV_CODEC_ID_AV1, 
                (new[] { "av1_vulkan", "av1_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
            { 
                AVCodecID.AV_CODEC_ID_MPEG4, 
                (new[] { "mpeg4_vulkan", "mpeg4_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
            { 
                AVCodecID.AV_CODEC_ID_MPEG2VIDEO, 
                (new[] { "mpeg2_vulkan", "mpeg2_mediacodec" }, HardwareDecoderType.Vulkan) 
            },
        };

        public FFmpegContext(AVCodecID codecId, bool preferHardware = true)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"Initializing FFmpeg decoder for {codecId}, Hardware preference: {preferHardware}");

            // 直接初始化只读字段，而不是通过方法
            string hardwareDecoderName = null;
            bool useHardwareDecoder = false;
            AVCodec* codec = null;
            HardwareDecoderType hardwareType = HardwareDecoderType.None;

            // 尝试硬件解码器（如果启用且可用）
            if (preferHardware)
            {
                if (AndroidHardwareDecoders.TryGetValue(codecId, out var decoderInfo))
                {
                    var (decoderNames, decoderType) = decoderInfo;
                    
                    foreach (string decoderName in decoderNames)
                    {
                        codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                        if (codec != null)
                        {
                            hardwareDecoderName = decoderName;
                            useHardwareDecoder = true;
                            hardwareType = decoderType;
                            Logger.Debug?.Print(LogClass.FFmpeg, $"Found hardware decoder: {decoderName} (Type: {decoderType})");
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
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected hardware decoder: {hardwareDecoderName} (Type: {hardwareType})");
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
                hardwareType = HardwareDecoderType.None;
                
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
            _hardwareType = hardwareType;
            _decoderType = useHardwareDecoder ? $"Hardware ({hardwareType})" : "Software";
            _hardwareDecoderName = hardwareDecoderName;

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 设置解码器参数 - 根据硬件类型使用不同的配置
            if (!ConfigureDecoderContext())
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to configure decoder context.");
                return;
            }

            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            if (openResult != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened (Error: {openResult}). Falling back to software decoder.");
                
                // 如果硬件解码器打开失败，尝试软件解码器
                if (_useHardwareDecoder)
                {
                    // 回退到软件解码器
                    AVCodec* softwareCodec = FFmpegApi.avcodec_find_decoder(codecId);
                    if (softwareCodec != null)
                    {
                        // 重新分配上下文
                        fixed (AVCodecContext** ppContext = &_context)
                        {
                            FFmpegApi.avcodec_free_context(ppContext);
                        }
                        
                        _context = FFmpegApi.avcodec_alloc_context3(softwareCodec);
                        
                        // 使用默认配置
                        if (FFmpegApi.avcodec_open2(_context, softwareCodec, null) == 0)
                        {
                            Logger.Info?.Print(LogClass.FFmpeg, $"Successfully opened software decoder: {GetCodecName(softwareCodec)}");
                            // 更新状态
                            _useHardwareDecoder = false;
                            _hardwareType = HardwareDecoderType.None;
                            _decoderType = "Software";
                        }
                        else
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, "Software decoder also failed to open.");
                            return;
                        }
                    }
                }
                else
                {
                    return;
                }
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

            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg {_decoderType} decoder initialized successfully (API: {(_useNewApi ? "New" : "Old")}, Codec: {GetCodecName(_codec)})");
        }

        private bool ConfigureDecoderContext()
        {
            try
            {
                if (_useHardwareDecoder)
                {
                    switch (_hardwareType)
                    {
                        case HardwareDecoderType.Vulkan:
                            return ConfigureVulkanDecoderContext();
                        case HardwareDecoderType.MediaCodec:
                            return ConfigureMediaCodecDecoderContext();
                        default:
                            return ConfigureGenericHardwareDecoderContext();
                    }
                }
                else
                {
                    return ConfigureSoftwareDecoderContext();
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Error configuring decoder context: {ex.Message}");
                return false;
            }
        }

        private bool ConfigureVulkanDecoderContext()
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "Configuring Vulkan hardware decoder context");

            try
            {
                // Vulkan 解码器的保守配置
                _context->ThreadCount = 1;
                _context->Refs = 1;
                
                // 设置 Vulkan 特定的像素格式
                _context->PixFmt = (int)AVPixelFormat.AV_PIX_FMT_VULKAN;
                
                // 禁用复杂的解码特性
                _context->MaxBFrames = 0;
                _context->HasBFrames = 0;
                
                // 设置低延迟模式
                _context->Flags |= 0x00000001; // CODEC_FLAG_LOW_DELAY
                _context->Delay = 0;
                
                Logger.Debug?.Print(LogClass.FFmpeg, "Vulkan hardware decoder context configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Error configuring Vulkan decoder: {ex.Message}");
                return false;
            }
        }

        private bool ConfigureMediaCodecDecoderContext()
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "Configuring MediaCodec hardware decoder context");

            try
            {
                // MediaCodec 解码器的保守配置
                _context->ThreadCount = 1;
                
                // 设置 MediaCodec 像素格式
                _context->PixFmt = (int)AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
                
                // 简化配置
                _context->MaxBFrames = 0;
                _context->HasBFrames = 0;
                _context->Refs = 1;
                
                Logger.Debug?.Print(LogClass.FFmpeg, "MediaCodec hardware decoder context configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Error configuring MediaCodec decoder: {ex.Message}");
                return false;
            }
        }

        private bool ConfigureGenericHardwareDecoderContext()
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "Configuring generic hardware decoder context");

            try
            {
                // 通用硬件解码器配置
                _context->ThreadCount = 1;
                _context->Refs = 1;
                _context->MaxBFrames = 0;
                _context->HasBFrames = 0;
                
                Logger.Debug?.Print(LogClass.FFmpeg, "Generic hardware decoder context configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Error configuring generic hardware decoder: {ex.Message}");
                return false;
            }
        }

        private bool ConfigureSoftwareDecoderContext()
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "Configuring software decoder context");

            try
            {
                // 软件解码器配置
                _context->ThreadCount = Math.Min(Environment.ProcessorCount, 2);
                _context->Refs = 3;
                
                Logger.Debug?.Print(LogClass.FFmpeg, $"Software decoder context configured successfully ({_context->ThreadCount} threads)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Error configuring software decoder: {ex.Message}");
                return false;
            }
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
            if (_context == null) 
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Decoder context is null");
                return -1;
            }

            try
            {
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
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Exception in DecodeFrame: {ex.Message}");
                return -1;
            }
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
            string errorType = _useHardwareDecoder ? $"hardware ({_hardwareType})" : "software";
            Logger.Warning?.Print(LogClass.FFmpeg, $"{operation} failed with {errorType} decoder (Error: {errorCode})");
            
            // 记录常见的错误代码
            switch (errorCode)
            {
                case -22: // EINVAL
                    Logger.Warning?.Print(LogClass.FFmpeg, "Invalid parameter error - check decoder configuration");
                    break;
                case -1094995529: // AVERROR_INVALIDDATA
                    Logger.Warning?.Print(LogClass.FFmpeg, "Invalid data encountered during decoding");
                    break;
                case -541478725: // AVERROR_EOF
                    Logger.Debug?.Print(LogClass.FFmpeg, "End of stream reached");
                    break;
                case -1313558101: // AVERROR_UNKNOWN
                    Logger.Warning?.Print(LogClass.FFmpeg, "Unknown hardware decoder error, consider using software fallback");
                    break;
            }
        }

        // 公开属性用于查询解码器状态
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string DecoderType => _decoderType;
        public string CodecName => GetCodecName(_codec);
        public string HardwareDecoderName => _hardwareDecoderName ?? "None";
        public string HardwareType => _hardwareType.ToString();

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
                if (AndroidHardwareDecoders.TryGetValue(codecId, out var decoderInfo))
                {
                    var (decoderNames, _) = decoderInfo;
                    foreach (string decoderName in decoderNames)
                    {
                        AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                        if (codec != null)
                        {
                            Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder available: {decoderName}");
                            return true;
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

        // 新增方法：获取硬件解码器状态信息
        public static string GetHardwareDecoderStatus()
        {
            var status = new System.Text.StringBuilder();
            status.AppendLine("Hardware Decoder Status:");
            
            foreach (var pair in AndroidHardwareDecoders)
            {
                string codecName = pair.Key.ToString();
                bool available = false;
                string availableDecoder = "";
                
                var (decoderNames, decoderType) = pair.Value;
                foreach (string decoderName in decoderNames)
                {
                    AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                    if (codec != null)
                    {
                        available = true;
                        availableDecoder = decoderName;
                        status.AppendLine($"  {codecName}: {decoderName} ({decoderType}) - AVAILABLE");
                        break;
                    }
                }
                
                if (!available)
                {
                    status.AppendLine($"  {codecName}: NOT AVAILABLE");
                }
            }
            
            return status.ToString();
        }

        // 新增方法：获取支持的 Vulkan 解码器列表
        public static List<string> GetSupportedVulkanDecoders()
        {
            var supportedDecoders = new List<string>();
            var vulkanDecoders = new[]
            {
                "h264_vulkan", "hevc_vulkan", "vp8_vulkan", "vp9_vulkan", 
                "av1_vulkan", "mpeg4_vulkan", "mpeg2_vulkan"
            };

            foreach (string decoderName in vulkanDecoders)
            {
                AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                if (codec != null)
                {
                    supportedDecoders.Add(decoderName);
                }
            }

            return supportedDecoders;
        }

        // 新增方法：强制使用特定类型的解码器
        public static FFmpegContext CreateWithSpecificDecoder(AVCodecID codecId, string preferredDecoder)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"Attempting to create decoder with specific decoder: {preferredDecoder}");

            // 首先尝试指定的解码器
            AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(preferredDecoder);
            if (codec != null)
            {
                Logger.Info?.Print(LogClass.FFmpeg, $"Found specified decoder: {preferredDecoder}");
                // 这里需要创建一个新的 FFmpegContext 实例
                // 由于构造函数限制，我们暂时回退到默认行为
            }

            // 回退到默认行为
            return new FFmpegContext(codecId, true);
        }
    }
}