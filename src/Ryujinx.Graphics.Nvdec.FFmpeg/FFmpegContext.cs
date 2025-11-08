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

        // Android 硬件解码器映射
        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_HEVC, new[] { "hevc_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP9, new[] { "vp9_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_AV1, new[] { "av1_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_MPEG4, new[] { "mpeg4_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_MPEG2VIDEO, new[] { "mpeg2_mediacodec" } },
        };

        // 导入设置 FFmpeg JNI 的 Native 函数
        [DllImport("ryujinxjni", CallingConvention = CallingConvention.Cdecl)]
        private static extern void setupFFmpegJNI();

        public FFmpegContext(AVCodecID codecId, bool preferHardware = true)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"Initializing FFmpeg decoder for {codecId}, Hardware preference: {preferHardware}");

            // 确保 FFmpeg JNI 环境已设置
            try
            {
                setupFFmpegJNI();
                Logger.Debug?.Print(LogClass.FFmpeg, "FFmpeg JNI environment setup completed");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to setup FFmpeg JNI: {ex.Message}");
            }

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
            ConfigureDecoderContext();

            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            
            // 详细的错误处理
            if (openResult != 0)
            {
                string errorDescription = GetFFmpegErrorDescription(openResult);
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened (Error: {openResult} - {errorDescription}). Falling back to software decoder.");
                
                // 如果硬件解码器打开失败，尝试软件解码器
                if (_useHardwareDecoder)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, "Attempting software decoder fallback...");
                    
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
                        
                        if (_context == null)
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate software codec context.");
                            return;
                        }
                        
                        // 配置软件解码器
                        ConfigureSoftwareDecoderContext();
                        
                        openResult = FFmpegApi.avcodec_open2(_context, softwareCodec, null);
                        if (openResult == 0)
                        {
                            Logger.Info?.Print(LogClass.FFmpeg, $"Successfully opened software decoder: {GetCodecName(softwareCodec)}");
                            // 更新解码器类型信息
                            _useHardwareDecoder = false;
                            _decoderType = "Software";
                            _hardwareDecoderName = null;
                        }
                        else
                        {
                            string softwareError = GetFFmpegErrorDescription(openResult);
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Software decoder also failed to open (Error: {openResult} - {softwareError}).");
                            return;
                        }
                    }
                    else
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, "No software decoder available for fallback.");
                        return;
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
                // 旧版 API 路径
                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                }
                else if (avCodecMajorVersion == 59)
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                }
                else
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                }
            }

            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg {_decoderType} decoder initialized successfully (API: {(_useNewApi ? "New" : "Old")}, Codec: {GetCodecName(_codec)})");
        }

        private string GetFFmpegErrorDescription(int errorCode)
        {
            // FFmpeg 错误码描述
            switch (errorCode)
            {
                case -1: return "Unknown error (possibly JNI/MediaCodec issue)";
                case -2: return "Codec not found";
                case -3: return "Invalid data";
                case -4: return "Buffer too small";
                case -5: return "End of file";
                case -6: return "Feature not supported";
                case -7: return "Invalid parameter";
                case -8: return "Memory allocation failed";
                case -9: return "Timeout";
                case -10: return "I/O error";
                case -11: return "Resource temporarily unavailable";
                case -12: return "Protocol not found";
                case -13: return "Stream not found";
                case -14: return "Bitstream filter not found";
                case -15: return "Decoder not found";
                case -16: return "Demuxer not found";
                case -17: return "Encoder not found";
                case -18: return "Option not found";
                case -19: return "Muxer not found";
                case -20: return "Filter not found";
                case -541478725: return "End of file (specific)";
                case -1094995529: return "Invalid data (specific)";
                default: return $"Unknown error code: {errorCode}";
            }
        }

        private void ConfigureDecoderContext()
        {
            // 基本解码器配置
            _context->ErrRecognition = 0x0001 | 0x0002 | 0x0004; // 多种错误识别标志
            _context->ErrorConcealment = 0x0001 | 0x0002; // 帧和边界错误隐藏
            _context->WorkaroundBugs = 1; // 启用bug规避

            if (_useHardwareDecoder)
            {
                // 硬件解码器优化设置
                _context->Flags2 |= 0x00000001; // CODEC_FLAG2_FAST - 快速解码
                _context->ThreadCount = 1; // 硬件解码器通常单线程
                
                // 减少错误恢复，硬件解码器通常更稳定
                _context->ErrRecognition = 0x0001;
                
                // 硬件解码器特定设置
                _context->Refs = 1; // 限制参考帧数量
                _context->SkipFrame = 0; // 不跳过帧
                _context->SkipIdct = 0; // 不跳过IDCT
                _context->SkipLoopFilter = 0; // 不跳过环路滤波
                
                Logger.Debug?.Print(LogClass.FFmpeg, "Configured for hardware decoding");
            }
            else
            {
                ConfigureSoftwareDecoderContext();
            }
        }

        private void ConfigureSoftwareDecoderContext()
        {
            // 软件解码器优化
            _context->ThreadCount = Math.Min(Environment.ProcessorCount, 4);
            _context->Refs = 3; // 限制参考帧数量
            _context->ErrRecognition = 0x0001 | 0x0002 | 0x0004;
            _context->ErrorConcealment = 0x0001 | 0x0002;
            _context->WorkaroundBugs = 1;
            
            Logger.Debug?.Print(LogClass.FFmpeg, $"Configured for software decoding ({_context->ThreadCount} threads)");
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
            string errorType = _useHardwareDecoder ? "hardware" : "software";
            string errorDescription = GetFFmpegErrorDescription(errorCode);
            Logger.Warning?.Print(LogClass.FFmpeg, $"{operation} failed with {errorType} decoder (Error: {errorCode} - {errorDescription})");
            
            if (_useHardwareDecoder && (errorCode == -1313558101 || errorCode == -1)) // AVERROR_UNKNOWN or generic error
            {
                Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decoder specific error, consider using software fallback");
            }
        }

        // 公开属性用于查询解码器状态
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string DecoderType => _decoderType;
        public string CodecName => GetCodecName(_codec);
        public string HardwareDecoderName => _hardwareDecoderName ?? "None";

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
    }
}