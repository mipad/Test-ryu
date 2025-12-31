// [file name]: AndroidFFmpegContext.cs
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// 专门为Android平台优化的FFmpeg硬件解码器上下文
    /// </summary>
    unsafe class FFmpegContext : IDisposable
    {
        private readonly AVCodec* _codec;
        private readonly AVCodecContext* _context;
        private readonly AVPacket* _packet;
        private readonly AVFrame* _frame;
        private readonly object _decodeLock = new();
        
        /// <summary>
        /// 是否使用MediaCodec硬件解码
        /// </summary>
        public bool UsingHardwareDecoder { get; private set; }

        /// <summary>
        /// 创建Android专用的解码器上下文
        /// </summary>
        /// <param name="codecId">编码格式ID</param>
        /// <param name="useMediaCodec">是否使用MediaCodec硬件解码</param>
        public FFmpegContext(AVCodecID codecId, bool useMediaCodec = true)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"Creating AndroidFFmpegContext for codec: {codecId}, useMediaCodec: {useMediaCodec}");

            // 1. 优先尝试MediaCodec硬件解码
            if (useMediaCodec && AndroidHardwareDecoder.IsSupportedByMediaCodec(codecId))
            {
                var decoderName = AndroidHardwareDecoder.GetMediaCodecDecoderName(codecId);
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Attempting to use MediaCodec decoder: {decoderName}");

                // 查找MediaCodec解码器
                _codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                
                if (_codec != null)
                {
                    UsingHardwareDecoder = true;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"Found MediaCodec decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");
                }
                else
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                        $"MediaCodec decoder {decoderName} not available, falling back to software");
                }
            }

            // 2. 如果MediaCodec不可用，使用软件解码器
            if (_codec == null)
            {
                _codec = FFmpegApi.avcodec_find_decoder(codecId);
                UsingHardwareDecoder = false;
                
                if (_codec == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec not found: {codecId}");
                    throw new InvalidOperationException($"Codec not found: {codecId}");
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Using software decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");
            }

            // 3. 创建编解码器上下文
            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate codec context");
                throw new OutOfMemoryException("Failed to allocate codec context");
            }

            // 4. 配置解码器参数（针对Android优化）
            ConfigureForAndroid();

            // 5. 打开编解码器
            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            if (openResult < 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Failed to open codec: {GetFFmpegError(openResult)}");
                throw new InvalidOperationException($"Failed to open codec: {GetFFmpegError(openResult)}");
            }

            // 6. 分配数据包和帧
            _packet = FFmpegApi.av_packet_alloc();
            _frame = FFmpegApi.av_frame_alloc();
            
            if (_packet == null || _frame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate packet or frame");
                throw new OutOfMemoryException("Failed to allocate packet or frame");
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"AndroidFFmpegContext created successfully. " +
                $"Using {(UsingHardwareDecoder ? "MediaCodec hardware" : "software")} decoder");
        }

        /// <summary>
        /// 为Android平台优化配置
        /// </summary>
        private void ConfigureForAndroid()
        {
            // Android特定的优化配置
            _context->ThreadCount = 1; // MediaCodec通常是单线程
            _context->ThreadType = 0;
            
            // 设置低延迟标志
            _context->Flags |= 0x0001; // CODEC_FLAG_LOW_DELAY
            
            // 针对移动设备优化
            _context->Flags2 |= 0x00000100; // AV_CODEC_FLAG2_FAST
            
            // 设置更适合移动设备的缓冲区大小
            _context->Delay = 0;
            _context->ActiveThreadType = 0;
            
            // 如果是H.264，设置一些优化参数
            if (UsingHardwareDecoder)
            {
                // MediaCodec可能需要这些设置
                _context->RefcountedFrames = 1;
                _context->PktTimebase = new AVRational { Num = 1, Den = 1000000 };
            }
        }

        /// <summary>
        /// 解码一帧
        /// </summary>
        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            lock (_decodeLock)
            {
                try
                {
                    // 清空帧
                    FFmpegApi.av_frame_unref(_frame);
                    FFmpegApi.av_frame_unref(output.Frame);

                    fixed (byte* ptr = bitstream)
                    {
                        // 设置数据包
                        _packet->Data = ptr;
                        _packet->Size = bitstream.Length;
                        
                        // 发送数据包
                        int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                        if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN)
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                $"avcodec_send_packet failed: {GetFFmpegError(sendResult)}");
                            return sendResult;
                        }
                        
                        // 清空数据包
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);
                        
                        // 接收帧
                        int receiveResult = FFmpegApi.avcodec_receive_frame(_context, _frame);
                        if (receiveResult < 0)
                        {
                            if (receiveResult == FFmpegApi.AVERROR.EAGAIN)
                                return -1; // 需要更多数据
                            if (receiveResult == FFmpegApi.AVERROR.EOF)
                                return 0; // 流结束
                                
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                $"avcodec_receive_frame failed: {GetFFmpegError(receiveResult)}");
                            return receiveResult;
                        }
                        
                        // 解码成功，复制到输出Surface
                        return CopyFrameToSurface(_frame, output.Frame);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                        $"Exception in DecodeFrame: {ex.Message}");
                    return -1;
                }
            }
        }

        /// <summary>
        /// 将解码后的帧复制到Surface
        /// </summary>
        private int CopyFrameToSurface(AVFrame* srcFrame, AVFrame* dstFrame)
        {
            // 复制基本信息
            dstFrame->Width = srcFrame->Width;
            dstFrame->Height = srcFrame->Height;
            dstFrame->Format = srcFrame->Format;
            
            // 复制数据平面（简化版，实际可能需要深拷贝）
            for (int i = 0; i < 4 && srcFrame->Data[i] != null; i++)
            {
                dstFrame->Data[i] = srcFrame->Data[i];
                dstFrame->LineSize[i] = srcFrame->LineSize[i];
            }
            
            return 0;
        }

        /// <summary>
        /// 获取FFmpeg错误信息
        /// </summary>
        private string GetFFmpegError(int errorCode)
        {
            byte* buffer = stackalloc byte[256];
            if (FFmpegApi.av_strerror(errorCode, buffer, 256) == 0)
            {
                return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"Error {errorCode}";
            }
            return $"Unknown error {errorCode}";
        }

        public void Dispose()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Disposing AndroidFFmpegContext");
            
            // 释放帧
            if (_frame != null)
            {
                FFmpegApi.av_frame_unref(_frame);
                FFmpegApi.av_free(_frame);
            }

            // 释放数据包
            if (_packet != null)
            {
                FFmpegApi.av_packet_unref(_packet);
                fixed (AVPacket** ppPacket = &_packet)
                {
                    FFmpegApi.av_packet_free(ppPacket);
                }
            }

            // 释放编解码器上下文
            if (_context != null)
            {
                FFmpegApi.avcodec_close(_context);
                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }
        }
    }
}
