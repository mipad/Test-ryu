using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;

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

            // 设置低延迟解码参数（类似yuzu）
            // 注意：需要在avcodec_open2之前设置
            // 注意：字段名是PascalCase，需要与AVCodecContext结构体一致
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "tune", "zerolatency", 0);
            _context->ThreadCount = 0; // 自动选择线程数
            _context->ThreadType &= ~FFmpegApi.FF_THREAD_FRAME; // 禁用帧级多线程

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
            
            int result = 0;
            
            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                
                // 发送数据包到解码器
                result = FFmpegApi.avcodec_send_packet(_context, _packet);
                
                if (result < 0 && result != FFmpegApi.AVERROR_EAGAIN)
                {
                    // 发送失败（非EAGAIN错误）
                    FFmpegApi.av_packet_unref(_packet);
                    FFmpegApi.av_frame_unref(output.Frame);
                    return result;
                }
            }
            
            FFmpegApi.av_packet_unref(_packet);
            
            // 尝试接收解码后的帧
            result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
            
            if (result == FFmpegApi.AVERROR_EAGAIN || result == FFmpegApi.AVERROR_EOF)
            {
                // 需要更多数据或已结束
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }
            else if (result < 0)
            {
                // 其他错误
                FFmpegApi.av_frame_unref(output.Frame);
                return result;
            }
            
            return 0; // 成功解码一帧
        }

        public void Dispose()
        {
            fixed (AVPacket** ppPacket = &_packet)
            {
                FFmpegApi.av_packet_free(ppPacket);
            }

            _ = FFmpegApi.avcodec_close(_context);

            fixed (AVCodecContext** ppContext = &_context)
            {
                FFmpegApi.avcodec_free_context(ppContext);
            }
        }
    }
}
