using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, AVFrame* frame, int* got_frame_ptr, AVPacket* avpkt);
        
        private readonly AVCodec_decode _decodeFrame;
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

            // 关键配置：完全禁用B帧和缓冲
            _context->HasBFrames = 0;
            _context->MaxBFrames = 0;
            _context->Refs = 1; // 只允许1个参考帧
            _context->Delay = 0; // 零延迟
            _context->ThreadCount = 1; // 单线程避免同步问题
            _context->ThreadType = 0; // 禁用多线程
            
            // 设置时间基为微秒，避免分数计算
            // 注意：字段名是 Numerator 和 Denominator，不是 Num 和 Den
            _context->TimeBase.Numerator = 1;
            _context->TimeBase.Denominator = 1000000;

            // 设置低延迟标志
            _context->Flags |= FFmpegApi.AV_CODEC_FLAG_LOW_DELAY;
            
            // 设置私有数据选项
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "tune", "zerolatency", 0);
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "preset", "ultrafast", 0);
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "sync", "ext", 0); // 使用外部时间戳

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

            // 检测FFmpeg版本并选择合适的解码函数
            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            // 根据版本选择正确的解码函数指针
            // 注意：这里需要确保 FFCodec、FFCodecLegacy 等结构体已正确定义
            if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
            {
                // 新版FFmpeg - 使用新的结构体布局
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
            }
            else if (avCodecMajorVersion == 59)
            {
                // 59.x版本 - 使用旧的结构体布局
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
            }
            else
            {
                // 58.x及更早版本
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
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
            Logger.Debug?.Print(LogClass.FFmpeg, 
                $"DecodeFrame: bitstream={bitstream.Length} bytes, PTS={output.Frame->Pts}, DTS={output.Frame->PktDts}");

            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame = 0;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                _packet->Pts = 0; // 强制设置PTS为0，避免时间戳问题
                _packet->Dts = 0;
                
                // 使用旧版API解码
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
            }

            // 强制清除任何延迟帧
            if (gotFrame == 0)
            {
                Logger.Debug?.Print(LogClass.FFmpeg, "No frame received, trying to flush decoder");
                
                // 如果有延迟帧，尝试获取
                _packet->Data = null;
                _packet->Size = 0;
                _packet->Pts = 0;
                _packet->Dts = 0;
                
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                
                // 无论是否成功，都清除B帧标志
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                Logger.Debug?.Print(LogClass.FFmpeg, "Still no frame received");
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }

            Logger.Debug?.Print(LogClass.FFmpeg, 
                $"Got frame: width={output.Frame->Width}, height={output.Frame->Height}, PTS={output.Frame->Pts}, result={result}");

            return result < 0 ? result : 0;
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
