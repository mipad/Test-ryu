using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        // 新版API委托
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        
        // 状态跟踪
        private int _frameCount = 0;
        private long _lastPts = -1;
        private bool _flushing = false;
        private bool _useNewApi = true; // 默认使用新版API

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

            // ============ 关键参数设置 ============
            
            // 1. 设置码率参数 - 这是最重要的！
            _context->BitRate = 5000000; // 5 Mbps，根据视频质量调整
            _context->RcBufferSize = 0; // 禁用缓冲区大小限制
            _context->RcMaxRate = _context->BitRate;
            _context->RcMinRate = _context->BitRate / 2;
            _context->BitRateTolerance = (int)(_context->BitRate * 0.1); // 10%容差
            
            // 2. 禁用B帧和重排序 - 减少延迟和抽搐
            _context->HasBFrames = 0;
            _context->MaxBFrames = 0;
            _context->GopSize = 12; // GOP大小设为12，平衡压缩和延迟
            
            // 3. 设置参考帧数量
            _context->Refs = 1; // 只使用1个参考帧
            
            // 4. 线程设置
            _context->ThreadCount = 1; // 单线程避免同步问题
            _context->ThreadType = 0; // 完全禁用多线程
            
            // 5. 时间相关设置
            _context->TimeBase.Numerator = 1;
            _context->TimeBase.Denominator = 90000; // 使用90kHz时钟
            _context->TicksPerFrame = 1;
            _context->Delay = 0; // 零解码延迟
            
            // 6. 质量相关设置
            _context->QMin = 2;
            _context->QMax = 31;
            _context->MaxQdiff = 3;
            
            // 7. 设置低延迟标志
            _context->Flags |= FFmpegApi.AV_CODEC_FLAG_LOW_DELAY;
            _context->Flags2 |= FFmpegApi.AV_CODEC_FLAG2_FAST; // 快速解码标志
            
            // 8. 设置私有选项
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "tune", "zerolatency", 0);
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "preset", "ultrafast", 0);
            FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "profile", "baseline", 0); // 使用基线配置减少复杂度
            
            // 对于H.264，设置特定选项
            if (codecId == AVCodecID.AV_CODEC_ID_H264)
            {
                FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "coder", "0", 0); // CABAC=0, CAVLC=1
                FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "flags", "+low_delay", 0);
                FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "flags2", "+fast", 0);
                FFmpegApi.av_opt_set(_context->PrivData.ToPointer(), "weightp", "0", 0); // 禁用加权预测
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
            
            // 检测FFmpeg版本，选择合适的API
            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            
            // 新版API更稳定，优先使用
            _useNewApi = avCodecMajorVersion >= 58;
            
            // 修复：将 useNewApi 改为 _useNewApi
            Logger.Info?.Print(LogClass.FFmpeg, 
                $"FFmpeg v{avCodecMajorVersion}, using {(_useNewApi ? "new" : "old")} API, " +
                $"bitrate={_context->BitRate}, refs={_context->Refs}, bframes={_context->MaxBFrames}");
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
            _frameCount++;
            
            // 每100帧记录一次状态
            if (_frameCount % 100 == 0)
            {
                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"Frame {_frameCount}: decoding {bitstream.Length} bytes, " +
                    $"last PTS={_lastPts}, flushing={_flushing}");
            }

            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            
            if (_useNewApi) // 使用成员变量 _useNewApi
            {
                result = DecodeWithNewApi(output, bitstream);
            }
            else
            {
                result = DecodeWithOldApi(output, bitstream);
            }
            
            // 更新最后PTS
            if (result == 0 && output.Frame->Pts > 0)
            {
                _lastPts = output.Frame->Pts;
            }

            return result;
        }

        private int DecodeWithNewApi(Surface output, ReadOnlySpan<byte> bitstream)
        {
            int result = 0;
            
            // 清理解码器缓冲（如果有）
            if (_flushing)
            {
                FlushDecoder();
                _flushing = false;
            }
            
            // 如果有数据，发送数据包
            if (bitstream.Length > 0)
            {
                fixed (byte* ptr = bitstream)
                {
                    // 设置数据包参数
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    
                    // 生成合理的PTS（基于帧计数）
                    long pts = _frameCount * 3000; // 假设30fps，每帧33ms≈3000时间单位
                    _packet->Pts = pts;
                    _packet->Dts = pts; // DTS和PTS相同，避免重排序
                    _packet->Duration = 3000;
                    
                    // 发送数据包
                    result = FFmpegApi.avcodec_send_packet(_context, _packet);
                    
                    if (result < 0 && result != FFmpegApi.AVERROR_EAGAIN)
                    {
                        Logger.Warning?.Print(LogClass.FFmpeg, 
                            $"avcodec_send_packet failed: {result}, size={bitstream.Length}");
                        FFmpegApi.av_packet_unref(_packet);
                        return result;
                    }
                }
                FFmpegApi.av_packet_unref(_packet);
            }
            else if (!_flushing)
            {
                // 开始刷新解码器
                _packet->Data = null;
                _packet->Size = 0;
                _packet->Pts = _frameCount * 3000;
                _packet->Dts = _packet->Pts;
                
                result = FFmpegApi.avcodec_send_packet(_context, _packet);
                _flushing = true;
            }
            
            // 接收帧
            result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
            
            if (result == 0)
            {
                // 成功接收到帧
                // 如果帧的PTS不合理，修正它
                if (output.Frame->Pts < _lastPts)
                {
                    output.Frame->Pts = _lastPts + 3000;
                }
                return 0;
            }
            else if (result == FFmpegApi.AVERROR_EAGAIN)
            {
                // 需要更多数据
                return -1;
            }
            else if (result == FFmpegApi.AVERROR_EOF)
            {
                // 解码器结束
                _flushing = false;
                return -1;
            }
            else
            {
                // 其他错误
                Logger.Warning?.Print(LogClass.FFmpeg, 
                    $"avcodec_receive_frame failed: {result}");
                return result;
            }
        }
        
        private void FlushDecoder()
        {
            // 清空解码器中的所有缓冲帧
            while (true)
            {
                AVFrame* tempFrame = FFmpegApi.av_frame_alloc();
                int flushResult = FFmpegApi.avcodec_receive_frame(_context, tempFrame);
                FFmpegApi.av_frame_unref(tempFrame);
                FFmpegApi.av_free(tempFrame);
                
                if (flushResult == FFmpegApi.AVERROR_EAGAIN || flushResult == FFmpegApi.AVERROR_EOF)
                    break;
            }
        }

        private int DecodeWithOldApi(Surface output, ReadOnlySpan<byte> bitstream)
        {
            // 恢复旧的解码委托（如果可用）
            // 这里需要根据实际的结构体定义来实现
            // 暂时用新版API替代
            Logger.Info?.Print(LogClass.FFmpeg, "Old API not implemented, using new API instead");
            return DecodeWithNewApi(output, bitstream);
        }

        public void Dispose()
        {
            // 发送空包刷新解码器
            if (!_flushing)
            {
                _packet->Data = null;
                _packet->Size = 0;
                FFmpegApi.avcodec_send_packet(_context, _packet);
                _flushing = true;
            }
            
            // 清理资源
            fixed (AVPacket** ppPacket = &_packet)
            {
                FFmpegApi.av_packet_free(ppPacket);
            }

            _ = FFmpegApi.avcodec_close(_context);

            fixed (AVCodecContext** ppContext = &_context)
            {
                FFmpegApi.avcodec_free_context(ppContext);
            }
            
            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpegContext disposed after {_frameCount} frames");
        }
    }
}
