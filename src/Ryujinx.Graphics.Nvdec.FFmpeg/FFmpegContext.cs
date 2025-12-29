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
        private bool _useHardwareDecoding;
        private bool _isMediaCodecDecoder;
        private bool _forceSoftwareDecode;
        private object _decodeLock = new object();

        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
        };

        public FFmpegContext(AVCodecID codecId)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            _forceSoftwareDecode = Environment.GetEnvironmentVariable("RYUJINX_FORCE_SOFTWARE_DECODE") == "1";
            
            _useHardwareDecoding = !_forceSoftwareDecode && ShouldUseHardwareDecoding(codecId);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled: {_useHardwareDecoding}");
            
            _codec = FindHardwareDecoder(codecId);
            if (_codec == null)
            {
                _codec = FFmpegApi.avcodec_find_decoder(codecId);
                if (_codec == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                    return;
                }
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found generic decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");
            }
            else
            {
                string codecName = Marshal.PtrToStringUTF8((IntPtr)_codec->Name) ?? "unknown";
                _isMediaCodecDecoder = codecName.Contains("mediacodec");
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found hardware decoder: {codecName}, IsMediaCodec: {_isMediaCodecDecoder}");
                
                if (_isMediaCodecDecoder && !_forceSoftwareDecode)
                {
                    _useHardwareDecoding = true;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "MediaCodec decoder detected, forcing hardware decoding");
                }
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            if (_context->PrivData != IntPtr.Zero)
            {
                FFmpegApi.av_opt_set((void*)_context->PrivData, "tune", "zerolatency", 0);
            }
            
            _context->ThreadCount = 0;
            _context->ThreadType &= ~2;
            
            if (_isMediaCodecDecoder && _useHardwareDecoding)
            {
                _context->ThreadCount = 1;
                _context->ThreadType = 0;
                _context->Flags |= 0x0001;
                _context->Flags2 |= 0x00000100;
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Set MediaCodec decoder options: single thread, low delay mode, fast decoding");
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            
            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec opened successfully. Pixel format: {_context->PixFmt}");

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using new FFmpeg API (avcodec_send_packet/avcodec_receive_frame)");
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext created successfully. IsMediaCodec: {_isMediaCodecDecoder}");
        }

        private unsafe AVCodec* FindHardwareDecoder(AVCodecID codecId)
        {
            if (!IsAndroidRuntime())
            {
                return null;
            }

            if (AndroidHardwareDecoders.TryGetValue(codecId, out var decoderNames))
            {
                foreach (var decoderName in decoderNames)
                {
                    var codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                    if (codec != null)
                    {
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found Android hardware decoder: {decoderName}");
                        return codec;
                    }
                    else
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Android hardware decoder not found: {decoderName}");
                    }
                }
            }

            return null;
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
                
                if (_isMediaCodecDecoder && _useHardwareDecoding && !_forceSoftwareDecode)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using MediaCodec hardware decoder");
                    return DecodeFrameMediaCodec(output, bitstream);
                }
                
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using software decoding path");
                return DecodeFrameSoftware(output, bitstream);
            }
        }
        
        private int DecodeFrameMediaCodec(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameMediaCodec called");
            
            if (output.Frame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Output frame is null");
                return -1;
            }
            
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
                    
                    // 总是使用新的API
                    int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    FFmpegApi.av_packet_unref(_packet);
                    
                    if (sendResult < 0)
                    {
                        if (sendResult == FFmpegApi.AVERROR.EAGAIN)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Decoder needs more packets (EAGAIN)");
                            return sendResult;
                        }
                        else if (sendResult == FFmpegApi.AVERROR.EOF)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "End of stream (EOF)");
                            return 0;
                        }
                        else
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {sendResult}");
                            return sendResult;
                        }
                    }
                    
                    int receiveResult = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                    
                    if (receiveResult == 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                            $"MediaCodec decode successful. Frame: Width={output.Frame->Width}, Height={output.Frame->Height}, " +
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
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception in DecodeFrameMediaCodec: {ex.Message}\n{ex.StackTrace}");
                return -1;
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
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Software decode completed successfully");
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
