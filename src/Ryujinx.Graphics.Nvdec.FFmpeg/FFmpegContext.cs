using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);

        private readonly AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private readonly bool _useNewApi;
        private bool _isFirstFrame = true;
        private bool _needsFlush = false;
        private System.Diagnostics.Stopwatch _decodeTimer = new System.Diagnostics.Stopwatch();
        private int _frameCount = 0;
        private readonly bool _useHardwareDecoder;
        private readonly string _decoderType;

        public FFmpegContext(AVCodecID codecId, bool preferHardware = true)
        {
            // 尝试硬件解码器（如果启用且可用）
            if (preferHardware)
            {
                _codec = FindHardwareDecoder(codecId);
                if (_codec != null)
                {
                    _useHardwareDecoder = true;
                    _decoderType = "Hardware";
                }
            }

            // 如果硬件解码器不可用，回退到软件解码器
            if (_codec == null)
            {
                _codec = FFmpegApi.avcodec_find_decoder(codecId);
                _useHardwareDecoder = false;
                _decoderType = "Software";
            }

            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }

            Logger.Info?.Print(LogClass.FFmpeg, $"Using {_decoderType} decoder: {GetCodecName(_codec)} for {codecId}");

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 设置解码器参数
            ConfigureDecoderContext();

            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened. Falling back to software decoder.");
                
                // 如果硬件解码器打开失败，尝试软件解码器
                if (_useHardwareDecoder)
                {
                    // 修复：使用 fixed 语句
                    fixed (AVCodecContext** ppContext = &_context)
                    {
                        FFmpegApi.avcodec_free_context(ppContext);
                    }
                    
                    // 回退到软件解码器
                    _codec = FFmpegApi.avcodec_find_decoder(codecId);
                    if (_codec != null)
                    {
                        _context = FFmpegApi.avcodec_alloc_context3(_codec);
                        ConfigureDecoderContext();
                        
                        if (FFmpegApi.avcodec_open2(_context, _codec, null) == 0)
                        {
                            _useHardwareDecoder = false;
                            _decoderType = "Software (Fallback)";
                            Logger.Info?.Print(LogClass.FFmpeg, $"Successfully opened software decoder: {GetCodecName(_codec)}");
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

            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}, using {(avCodecMajorVersion >= 58 ? "new" : "old")} API");

            // 检测是否使用新版 API (avcodec_send_packet/avcodec_receive_frame)
            _useNewApi = avCodecMajorVersion >= 58;

            if (!_useNewApi)
            {
                // 旧版 API 路径
                // libavcodec 59.24 changed AvCodec to move its private API and also move the codec function to an union.
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

            // 修复拼写错误：Logclass -> LogClass
            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg {_decoderType} decoder initialized successfully (API: {(_useNewApi ? "New" : "Old")})");
        }

        private AVCodec* FindHardwareDecoder(AVCodecID codecId)
        {
            // 硬件解码器名称映射
            string hardwareDecoderName = codecId switch
            {
                AVCodecID.AV_CODEC_ID_H264 => "h264_mediacodec",
                AVCodecID.AV_CODEC_ID_HEVC => "hevc_mediacodec",
                AVCodecID.AV_CODEC_ID_VP8 => "vp8_mediacodec",
                AVCodecID.AV_CODEC_ID_VP9 => "vp9_mediacodec",
                AVCodecID.AV_CODEC_ID_AV1 => "av1_mediacodec",
                AVCodecID.AV_CODEC_ID_MPEG4 => "mpeg4_mediacodec",
                AVCodecID.AV_CODEC_ID_MPEG2VIDEO => "mpeg2_mediacodec",
                _ => null
            };

            if (hardwareDecoderName != null)
            {
                AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(hardwareDecoderName);
                if (codec != null)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"Found hardware decoder: {hardwareDecoderName}");
                    return codec;
                }
            }

            Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder not available for {codecId}, falling back to software");
            return null;
        }

        private void ConfigureDecoderContext()
        {
            // 设置错误恢复选项
            _context->ErrRecognition = 0x0001 | 0x0002 | 0x0004; // 多种错误识别标志
            _context->ErrorConcealment = 0x0001 | 0x0002; // 帧和边界错误隐藏
            _context->SkipFrame = (int)AVDiscard.Default;
            _context->SkipIdct = (int)AVDiscard.Default;
            _context->SkipLoopFilter = (int)AVDiscard.Default;
            _context->WorkaroundBugs = 1; // 启用bug规避

            // 硬件解码器特定的设置
            if (_useHardwareDecoder)
            {
                // 硬件解码器通常需要较少的错误恢复
                _context->ErrRecognition = 0x0001; // 仅基本错误识别
                _context->Flags2 |= 0x00000001; // CODEC_FLAG2_FAST - 快速解码
                
                // 限制线程数以与硬件解码器更好配合
                _context->ThreadCount = 1;
            }
            else
            {
                // 软件解码器优化
                _context->ThreadCount = Math.Min(Environment.ProcessorCount, 4); // 限制最大线程数
                _context->Refs = 3; // 限制参考帧数量
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

            string line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer).Trim();

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

            // 每100帧输出一次性能统计
            if (_frameCount % 100 == 0)
            {
                double avgTime = _decodeTimer.Elapsed.TotalMilliseconds / _frameCount;
                double fps = 1000.0 / avgTime;
                Logger.Debug?.Print(LogClass.FFmpeg, $"{_decoderType} decode performance: {avgTime:F2}ms per frame ({fps:F1} FPS)");
                
                // 重置计时器
                _decodeTimer.Reset();
                _frameCount = 0;
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
                    Logger.Warning?.Print(LogClass.FFmpeg, $"avcodec_send_packet failed with error: {result}");
                    
                    // 如果是硬件解码器失败，可能需要特殊处理
                    if (_useHardwareDecoder)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decoder failed, consider falling back to software");
                    }
                    
                    // 标记需要刷新解码器
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
                    result = 0; // 这些不是错误，只是状态
                    _isFirstFrame = false;
                }
                else
                {
                    // 解码错误，尝试恢复
                    Logger.Warning?.Print(LogClass.FFmpeg, $"avcodec_receive_frame failed with error: {result}");
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

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
            }

            if (result < 0)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"DecodeFrame failed with error: {result}");
                
                if (_isFirstFrame || result == -1094995529) // AVERROR_INVALIDDATA
                {
                    // 第一个帧解码失败或数据无效，标记需要刷新
                    _needsFlush = true;
                    _isFirstFrame = false;
                    FFmpegApi.av_packet_unref(_packet);
                    FFmpegApi.av_frame_unref(output.Frame);
                    return -1;
                }
            }

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);

                // If the frame was not delivered, it was probably delayed.
                // Get the next delayed frame by passing a 0 length packet.
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);

                // We need to set B frames to 0 as we already consumed all delayed frames.
                // This prevents the decoder from trying to return a delayed frame next time.
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }

            _isFirstFrame = false;
            return result < 0 ? result : 0;
        }

        // 公开属性用于查询解码器状态
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string DecoderType => _decoderType;
        public string CodecName => GetCodecName(_codec);

        public void Dispose()
        {
            fixed (AVPacket** ppPacket = &_packet)
            {
                FFmpegApi.av_packet_free(ppPacket);
            }

            // 在新版 FFmpeg 中，avcodec_close 已被弃用，使用 avcodec_free_context 即可
            // 如果需要刷新解码器缓冲区，可以添加：
            if (_useNewApi)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
            }

            fixed (AVCodecContext** ppContext = &_context)
            {
                FFmpegApi.avcodec_free_context(ppContext);
            }
            
            Logger.Debug?.Print(LogClass.FFmpeg, $"{_decoderType} decoder disposed");
        }
    }
}
