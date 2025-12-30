using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private enum DecodeAPIVersion
        {
            OldAPI,     // 使用 avcodec_decode_video2
            LegacyAPI,  // 使用 FFCodecLegacy
            NewAPI      // 使用 avcodec_send_packet/avcodec_receive_frame (推荐)
        }

        private DecodeAPIVersion _apiVersion; // 移除了 readonly
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private readonly object _decodeLock = new();
        private bool _useNewAPI = true; // 默认使用新API
        private int _threadCount = 0;   // 0表示自动检测
        private bool _lowLatency = false;
        private bool _fastDecode = false;

        // 用于旧API的委托
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);
        private AVCodec_decode _decodeFrame; // 移除了 readonly

        // 日志回调
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;

        public FFmpegContext(AVCodecID codecId, bool enableMultithreading = true, bool useNewAPI = true, int threadCount = 0)
        {
            _useNewAPI = useNewAPI;
            _threadCount = threadCount;

            // 设置性能优化标志
            _lowLatency = true; // 启用低延迟模式
            _fastDecode = true; // 启用快速解码

            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                // 尝试通过名称查找
                string codecName = codecId switch
                {
                    AVCodecID.AV_CODEC_ID_H264 => "h264",
                    AVCodecID.AV_CODEC_ID_VP8 => "vp8",
                    _ => null
                };

                if (codecName != null)
                {
                    _codec = FFmpegApi.avcodec_find_decoder_by_name(codecName);
                }

                if (_codec == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec {codecId} wasn't found. Make sure you have the codec present in your FFmpeg installation.");
                    return;
                }
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 配置解码器参数以优化性能
            ConfigureDecoderContext(enableMultithreading);

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

            // 检测FFmpeg版本并选择合适的API
            DetectAPIVersion();
        }

        private void ConfigureDecoderContext(bool enableMultithreading)
        {
            // 启用多线程解码
            if (enableMultithreading)
            {
                if (_threadCount <= 0)
                {
                    // 自动检测CPU核心数，留1个核心给系统
                    _threadCount = Math.Max(1, Environment.ProcessorCount - 1);
                }

                _context->ThreadCount = _threadCount;
                _context->ThreadType = 2; // FF_THREAD_SLICE (更高效)
            }

            // 性能优化标志
            if (_lowLatency)
            {
                _context->Flags |= (1 << 0); // CODEC_FLAG_LOW_DELAY
            }

            if (_fastDecode)
            {
                _context->Flags2 |= (1 << 15); // AV_CODEC_FLAG2_FAST
            }

            // 跳过非参考帧以加速解码
            _context->SkipFrame = 1; // AVDISCARD_NONREF
            _context->SkipIdct = 1;  // AVDISCARD_NONREF
            _context->SkipLoopFilter = 1; // AVDISCARD_NONREF

            // 设置解码器属性
            _context->WorkaroundBugs = 0;
            _context->ErrorConcealment = 3; // FF_EC_GUESS_MVS | FF_EC_DEBLOCK
        }

        private void DetectAPIVersion()
        {
            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}");

            if (_useNewAPI)
            {
                // 强制使用新API
                _apiVersion = DecodeAPIVersion.NewAPI;
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using new API (avcodec_send_packet/avcodec_receive_frame)");
            }
            else
            {
                // 根据版本自动选择API
                if (avCodecMajorVersion >= 60 || (avCodecMajorVersion == 59 && avCodecMinorVersion >= 37))
                {
                    // FFmpeg 6.x 或 5.37+ 推荐使用新API
                    _apiVersion = DecodeAPIVersion.NewAPI;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using new API (auto-detected for FFmpeg 6.x/5.37+)");
                }
                else if (avCodecMajorVersion == 59)
                {
                    // FFmpeg 5.x (59.x)
                    _apiVersion = DecodeAPIVersion.LegacyAPI;
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using legacy API for FFmpeg 5.x");
                }
                else if (avCodecMajorVersion >= 58)
                {
                    // FFmpeg 4.x (58.x)
                    _apiVersion = DecodeAPIVersion.OldAPI;
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using old API for FFmpeg 4.x");
                }
                else
                {
                    // 非常旧的版本
                    _apiVersion = DecodeAPIVersion.OldAPI;
                    // 这里可能需要不同的结构体
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Using old API for legacy FFmpeg version");
                }
            }
        }

        static FFmpegContext()
        {
            _logFunc = Log;
            
            // 设置日志级别和回调
            FFmpegApi.av_log_set_level(AVLog.Info); // 只记录重要信息
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

            string line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer)?.Trim();
            if (string.IsNullOrEmpty(line))
                return;

            // 过滤掉过于详细的调试信息
            if (line.Contains("[h264") || line.Contains("[hevc") || line.Contains("nal_unit_type"))
            {
                if (level <= AVLog.Warning) // 只记录警告及以上
                    Logger.Debug?.Print(LogClass.FFmpeg, line);
                return;
            }

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
                    // 忽略Trace级别日志，避免性能影响
                    break;
            }
        }

        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            lock (_decodeLock)
            {
                FFmpegApi.av_frame_unref(output.Frame);

                if (_apiVersion == DecodeAPIVersion.NewAPI)
                {
                    return DecodeFrameNewAPI(output, bitstream);
                }
                else
                {
                    return DecodeFrameOldAPI(output, bitstream);
                }
            }
        }

        private int DecodeFrameNewAPI(Surface output, ReadOnlySpan<byte> bitstream)
        {
            int result;
            bool hasPacket = bitstream.Length > 0;

            // 发送数据包
            if (hasPacket)
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    result = FFmpegApi.avcodec_send_packet(_context, _packet);
                }
            }
            else
            {
                // 发送空包以刷新解码器
                _packet->Data = null;
                _packet->Size = 0;
                result = FFmpegApi.avcodec_send_packet(_context, _packet);
            }

            if (result < 0 && result != -11) // -11 = AVERROR(EAGAIN)
            {
                FFmpegApi.av_packet_unref(_packet);
                return result;
            }

            // 接收帧
            result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
            
            // 清理数据包
            if (hasPacket)
            {
                FFmpegApi.av_packet_unref(_packet);
            }

            if (result == 0)
            {
                // 成功解码一帧
                return 0;
            }
            else if (result == -11) // AVERROR(EAGAIN) - 需要更多数据
            {
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }
            else
            {
                // 其他错误
                FFmpegApi.av_frame_unref(output.Frame);
                return result;
            }
        }

        private int DecodeFrameOldAPI(Surface output, ReadOnlySpan<byte> bitstream)
        {
            int result;
            int gotFrame;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
            }

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);

                // 如果帧未输出，可能是延迟帧
                // 传递空包获取延迟帧
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);

                // 重置B帧标志
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }

            return result < 0 ? result : 0;
        }

        public void Flush()
        {
            lock (_decodeLock)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
            }
        }

        public void SetThreadCount(int threadCount)
        {
            if (threadCount > 0 && threadCount <= 64)
            {
                _threadCount = threadCount;
                // 注意：这里需要重新打开解码器才能生效
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Thread count set to {threadCount} (requires decoder restart)");
            }
        }

        public string GetCodecInfo()
        {
            if (_codec == null)
                return "No codec loaded";

            string name = Marshal.PtrToStringAnsi((IntPtr)_codec->Name) ?? "Unknown";
            string longName = Marshal.PtrToStringAnsi((IntPtr)_codec->LongName) ?? "Unknown";
            
            return $"Codec: {name} ({longName}), Threads: {_threadCount}, API: {_apiVersion}";
        }

        public void Dispose()
        {
            lock (_decodeLock)
            {
                if (_packet != null)
                {
                    fixed (AVPacket** ppPacket = &_packet)
                    {
                        FFmpegApi.av_packet_free(ppPacket);
                    }
                }

                if (_context != null)
                {
                    _ = FFmpegApi.avcodec_close(_context);

                    fixed (AVCodecContext** ppContext = &_context)
                    {
                        FFmpegApi.avcodec_free_context(ppContext);
                    }
                }
            }

            GC.SuppressFinalize(this);
        }

        ~FFmpegContext()
        {
            Dispose();
        }
    }
}
