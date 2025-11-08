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

        public FFmpegContext(AVCodecID codecId)
        {
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

            // 设置更宽松的错误恢复选项
            _context->ErrorRecovery = 1; // FF_ER_CAREFUL
            _context->SkipFrame = AVDiscard.Default;
            _context->SkipIdct = AVDiscard.Default;
            _context->SkipLoopFilter = AVDiscard.Default;

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

            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

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
            FFmpegApi.av_frame_unref(output.Frame);

            if (_useNewApi)
            {
                // 使用新版 API: avcodec_send_packet/avcodec_receive_frame
                return DecodeFrameNewApi(output, bitstream);
            }
            else
            {
                // 使用旧版 API
                return DecodeFrameOldApi(output, bitstream);
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
                    // 如果是第一个帧且出错，尝试刷新解码器
                    if (_isFirstFrame)
                    {
                        FFmpegApi.avcodec_flush_buffers(_context);
                        _isFirstFrame = false;
                        FFmpegApi.av_packet_unref(_packet);
                        return -1; // 跳过这个帧
                    }
                    FFmpegApi.av_packet_unref(_packet);
                    return result;
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
                    if (_isFirstFrame)
                    {
                        FFmpegApi.avcodec_flush_buffers(_context);
                    }
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

            if (result < 0 && _isFirstFrame)
            {
                // 第一个帧解码失败，尝试恢复
                FFmpegApi.avcodec_flush_buffers(_context);
                _isFirstFrame = false;
                FFmpegApi.av_packet_unref(_packet);
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
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
        }
    }
}
