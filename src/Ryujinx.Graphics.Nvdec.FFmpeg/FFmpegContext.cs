using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        
        // 帧队列，用于处理延迟帧
        private readonly Queue<IntPtr> _frameQueue = new Queue<IntPtr>();
        private bool _flushing = false;

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

            // 设置解码器参数以优化视频解码
            // 禁用B帧以减少延迟和重排序问题
            _context->MaxBFrames = 0;
            _context->HasBFrames = 0; // 明确设置没有B帧
            
            // 设置低延迟解码参数
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "tune", "zerolatency", 0);
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "preset", "ultrafast", 0);
            
            // 设置线程参数
            _context->ThreadCount = 1; // 固定为1线程，避免同步问题
            _context->ThreadType &= ~FFmpegApi.FF_THREAD_FRAME; // 禁用帧级多线程
            
            // 设置低延迟标志
            _context->Flags |= FFmpegApi.AV_CODEC_FLAG_LOW_DELAY;
            
            // 设置其他优化参数
            _context->Refs = 1; // 限制参考帧数量
            _context->Delay = 0; // 设置解码延迟为0

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
            // 首先检查帧队列中是否有缓存的帧
            if (_frameQueue.Count > 0)
            {
                IntPtr framePtr = _frameQueue.Dequeue();
                AVFrame* cachedFrame = (AVFrame*)framePtr;
                
                // 将缓存的帧复制到输出
                CopyFrame(output.Frame, cachedFrame);
                FFmpegApi.av_frame_unref(cachedFrame);
                FFmpegApi.av_free(cachedFrame);
                return 0;
            }

            FFmpegApi.av_frame_unref(output.Frame);
            
            int result = 0;
            bool hasData = bitstream.Length > 0;
            
            if (hasData)
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    _packet->Pts = 0; // 设置PTS，避免解码器使用内部时间戳
                    
                    // 发送数据包到解码器
                    result = FFmpegApi.avcodec_send_packet(_context, _packet);
                    
                    if (result < 0 && result != FFmpegApi.AVERROR_EAGAIN)
                    {
                        // 发送失败（非EAGAIN错误）
                        FFmpegApi.av_packet_unref(_packet);
                        return result;
                    }
                }
                FFmpegApi.av_packet_unref(_packet);
                _flushing = false;
            }
            else if (!_flushing)
            {
                // 发送空包刷新解码器
                _packet->Data = null;
                _packet->Size = 0;
                _packet->Pts = 0;
                result = FFmpegApi.avcodec_send_packet(_context, _packet);
                _flushing = true;
            }

            // 尝试接收解码后的帧
            AVFrame* decodedFrame = FFmpegApi.av_frame_alloc();
            result = FFmpegApi.avcodec_receive_frame(_context, decodedFrame);
            
            while (result == 0)
            {
                // 成功接收到一帧
                if (_frameQueue.Count == 0)
                {
                    // 第一帧直接返回
                    CopyFrame(output.Frame, decodedFrame);
                    FFmpegApi.av_frame_unref(decodedFrame);
                    FFmpegApi.av_free(decodedFrame);
                    return 0;
                }
                else
                {
                    // 后续帧缓存起来
                    AVFrame* cachedFrame = FFmpegApi.av_frame_alloc();
                    CopyFrame(cachedFrame, decodedFrame);
                    _frameQueue.Enqueue((IntPtr)cachedFrame);
                    FFmpegApi.av_frame_unref(decodedFrame);
                    
                    // 继续接收下一帧
                    decodedFrame = FFmpegApi.av_frame_alloc();
                    result = FFmpegApi.avcodec_receive_frame(_context, decodedFrame);
                }
            }
            
            FFmpegApi.av_frame_unref(decodedFrame);
            FFmpegApi.av_free(decodedFrame);
            
            if (result == FFmpegApi.AVERROR_EAGAIN || result == FFmpegApi.AVERROR_EOF)
            {
                // 需要更多数据或已结束
                return -1;
            }
            else if (result < 0)
            {
                // 其他错误
                return result;
            }
            
            return 0;
        }

        private unsafe void CopyFrame(AVFrame* dst, AVFrame* src)
        {
            // 使用av_frame_ref复制帧，这会增加引用计数，而不是深拷贝
            int ret = FFmpegApi.av_frame_ref(dst, src);
            if (ret < 0)
            {
                // 如果av_frame_ref失败，手动复制关键字段
                dst->Width = src->Width;
                dst->Height = src->Height;
                dst->Format = src->Format;
                dst->Pts = src->Pts;
                dst->PktDts = src->PktDts;
                dst->SampleAspectRatio = src->SampleAspectRatio;
                
                // 复制数据平面
                for (int i = 0; i < 8; i++)
                {
                    dst->Data[i] = src->Data[i];
                    dst->LineSize[i] = src->LineSize[i];
                }
            }
        }

        public void Dispose()
        {
            // 清理帧队列
            while (_frameQueue.Count > 0)
            {
                IntPtr framePtr = _frameQueue.Dequeue();
                AVFrame* frame = (AVFrame*)framePtr;
                FFmpegApi.av_frame_unref(frame);
                FFmpegApi.av_free(frame);
            }

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
