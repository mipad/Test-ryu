using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private IntPtr _hwDeviceCtx;
        private bool _useHardwareDecoding;
        private bool _isMediaCodecDecoder;
        private bool _forceSoftwareDecode;
        private object _decodeLock = new object();
        private AVFrame* _hwFrame;

        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
        };

        public FFmpegContext(AVCodecID codecId)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            _forceSoftwareDecode = Environment.GetEnvironmentVariable("RYUJINX_FORCE_SOFTWARE_DECODE") == "1";
            
            // 暂时禁用硬件解码，因为需要更多配置
            _useHardwareDecoding = false;
            
            if (!_forceSoftwareDecode && ShouldUseHardwareDecoding(codecId))
            {
                _useHardwareDecoding = false; // 暂时禁用，需要实现完整的MediaCodec支持
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "MediaCodec hardware decoding requires full implementation, using software fallback");
            }
            
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }
            
            string codecName = Marshal.PtrToStringUTF8((IntPtr)_codec->Name) ?? "unknown";
            _isMediaCodecDecoder = codecName.Contains("mediacodec");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found decoder: {codecName}, IsMediaCodec: {_isMediaCodecDecoder}");

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated codec context: 0x{(ulong)_context:X}");

            // 如果是MediaCodec解码器但不需要硬件解码，或者硬件解码未启用，使用软件解码路径
            if (!_useHardwareDecoding && _isMediaCodecDecoder)
            {
                // 设置软件解码参数
                _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Setting MediaCodec decoder to software mode (no hardware acceleration)");
            }

            if (_context->PrivData != IntPtr.Zero)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Setting zero latency tune, PrivData: 0x{(ulong)_context->PrivData:X}");
                FFmpegApi.av_opt_set((void*)_context->PrivData, "tune", "zerolatency", 0);
            }
            
            _context->ThreadCount = 0;
            _context->ThreadType &= ~2;
            
            if (_isMediaCodecDecoder)
            {
                _context->ThreadCount = 1;
                _context->ThreadType = 0;
                _context->Flags |= 0x0001;
                _context->Flags2 |= 0x00000100;
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Set MediaCodec decoder options: single thread, low delay mode, fast decoding");
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            
            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_open2 result: {openResult}");
            
            if (openResult != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened. Error code: {openResult}");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec opened successfully. Pixel format: {_context->PixFmt}");

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated packet: 0x{(ulong)_packet:X}");

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using new FFmpeg API (avcodec_send_packet/avcodec_receive_frame)");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext created successfully. IsMediaCodec: {_isMediaCodecDecoder}, HardwareDecoding: {_useHardwareDecoding}");
        }

        private bool ShouldUseHardwareDecoding(AVCodecID codecId)
        {
            bool isAndroid = IsAndroidRuntime();
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Checking hardware decoding for codec {codecId} on platform: Android={isAndroid}, RID={RuntimeInformation.RuntimeIdentifier}");
            
            bool platformSupported = isAndroid;
            
            if (!platformSupported)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoding disabled: Not on Android platform");
                return false;
            }
                
            if (codecId != AVCodecID.AV_CODEC_ID_H264 && codecId != AVCodecID.AV_CODEC_ID_VP8)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding disabled: Codec {codecId} not supported for hardware decoding (only H264 and VP8)");
                return false;
            }
                
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled for codec {codecId} on Android platform");
            return true;
        }

        private bool IsAndroidRuntime()
        {
            string rid = RuntimeInformation.RuntimeIdentifier.ToLowerInvariant();
            
            if (rid.Contains("android") || rid.Contains("bionic"))
            {
                return true;
            }
            
            if (Environment.GetEnvironmentVariable("ANDROID_ROOT") != null ||
                Environment.GetEnvironmentVariable("ANDROID_DATA") != null)
            {
                return true;
            }
            
            return false;
        }

        static FFmpegContext()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpegContext static constructor called");
            
            _logFunc = Log;

            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(_logFunc);
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpeg logging initialized");
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
            lock (_decodeLock)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"DecodeFrame called. Bitstream size: {bitstream.Length}, IsMediaCodec: {_isMediaCodecDecoder}");
                
                // 总是使用软件解码路径，直到我们实现完整的硬件解码支持
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using software decoding path");
                return DecodeFrameSoftware(output, bitstream);
            }
        }
        
        private int DecodeFrameSoftware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameSoftware called");
            
            FFmpegApi.av_frame_unref(output.Frame);
            
            if (!output.AllocateBuffer())
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate output frame buffer");
                return -1;
            }

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decoding packet with size: {bitstream.Length}");
                    
                    int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    FFmpegApi.av_packet_unref(_packet);
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {sendResult}");
                        return sendResult;
                    }
                    
                    int receiveResult = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                    
                    if (receiveResult == 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                            $"Decode completed successfully. Frame: Width={output.Frame->Width}, Height={output.Frame->Height}, " +
                            $"Format={output.Frame->Format}, Linesize0={output.Frame->LineSize[0]}");
                        return 0;
                    }
                    else if (receiveResult == FFmpegApi.AVERROR.EAGAIN)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, "No frame available yet (EAGAIN)");
                        return -1;
                    }
                    else if (receiveResult == FFmpegApi.AVERROR.EOF)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, "End of stream (EOF)");
                        return 0;
                    }
                    else
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame failed: {receiveResult}");
                        return receiveResult;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception in DecodeFrameSoftware: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }

        public void Dispose()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Disposing FFmpegContext");
            
            fixed (AVPacket** ppPacket = &_packet)
            {
                FFmpegApi.av_packet_free(ppPacket);
            }

            if (_context != null)
            {
                _ = FFmpegApi.avcodec_close(_context);

                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpegContext disposed");
        }
    }
}
