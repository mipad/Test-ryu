using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);
        
        private readonly AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        
        private AVCodec* _codec;
        private AVPacket* _packet;
        private AVCodecContext* _context;
        
        private AVBufferRef* _hw_device_ctx = null;
        private bool _useHardwareAcceleration = false;
        
        private static readonly bool EnableDebugLogs = true;

        public FFmpegContext(AVCodecID codecId)
        {
            LogInfo($"Initializing FFmpegContext for codec: {codecId}");
            
            if (InitializeHardwareDecoder(codecId))
            {
                _useHardwareAcceleration = true;
                LogInfo("Hardware decoder initialized successfully");
            }
            else
            {
                LogInfo("Hardware decoder initialization failed, using software decoder");
                InitializeSoftwareDecoder(codecId);
            }
            
            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                LogError("Failed to allocate packet");
                throw new OutOfMemoryException("Failed to allocate AVPacket");
            }
            
            SetupDecodeFunction();
            
            LogInfo($"FFmpegContext initialized successfully: HardwareAcceleration={_useHardwareAcceleration}");
        }

        private bool InitializeHardwareDecoder(AVCodecID codecId)
        {
            try
            {
                LogInfo("Attempting to initialize hardware decoder");
                
                string hardwareDecoderName = GetHardwareDecoderName(codecId);
                if (hardwareDecoderName == null)
                {
                    LogInfo($"No hardware decoder available for {codecId}");
                    return false;
                }
                
                LogInfo($"Looking for hardware decoder: {hardwareDecoderName}");
                
                AVCodec* hwCodec = FFmpegApi.avcodec_find_decoder_by_name(hardwareDecoderName);
                if (hwCodec == null)
                {
                    LogInfo($"Hardware decoder {hardwareDecoderName} not found");
                    return false;
                }
                
                string codecName = Marshal.PtrToStringAnsi((IntPtr)hwCodec->Name) ?? "Unknown";
                LogInfo($"Found hardware decoder: {codecName}");
                
                if (!CreateHardwareDeviceContext())
                {
                    LogInfo("Failed to create hardware device context");
                    return false;
                }
                
                _context = FFmpegApi.avcodec_alloc_context3(hwCodec);
                if (_context == null)
                {
                    LogError("Failed to allocate codec context");
                    return false;
                }
                
                _context->HwDeviceCtx = FFmpegApi.av_buffer_ref(_hw_device_ctx);
                if (_context->HwDeviceCtx == null)
                {
                    LogError("Failed to set hardware device context");
                    return false;
                }
                
                _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
                
                int openResult = FFmpegApi.avcodec_open2(_context, hwCodec, null);
                if (openResult < 0)
                {
                    LogError($"Failed to open hardware codec: {GetErrorDescription(openResult)}");
                    return false;
                }
                
                _codec = hwCodec;
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Exception in InitializeHardwareDecoder: {ex.Message}");
                return false;
            }
        }

        private bool CreateHardwareDeviceContext()
        {
            try
            {
                LogInfo("Creating MediaCodec hardware device context");
                
                AVDictionary* opts = null;
                
                int result = FFmpegApi.av_dict_set(&opts, "create_window", "1", 0);
                if (result < 0)
                {
                    LogError("Failed to set dictionary option");
                }
                
                AVBufferRef* device_ref = null;
                result = FFmpegApi.av_hwdevice_ctx_create(&device_ref, 
                    FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC,
                    null, opts, 0);
                
                if (opts != null)
                {
                    FFmpegApi.av_dict_free(&opts);
                }
                
                if (result < 0)
                {
                    LogError($"av_hwdevice_ctx_create failed: {GetErrorDescription(result)}");
                    return false;
                }
                
                _hw_device_ctx = device_ref;
                
                result = FFmpegApi.av_hwdevice_ctx_init(_hw_device_ctx);
                if (result < 0)
                {
                    LogError($"av_hwdevice_ctx_init failed: {GetErrorDescription(result)}");
                    return false;
                }
                
                LogInfo("Hardware device context created and initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Exception in CreateHardwareDeviceContext: {ex.Message}");
                return false;
            }
        }

        private void InitializeSoftwareDecoder(AVCodecID codecId)
        {
            try
            {
                LogInfo("Initializing software decoder");
                
                _codec = FFmpegApi.avcodec_find_decoder(codecId);
                if (_codec == null)
                {
                    throw new InvalidOperationException($"Codec {codecId} not found");
                }
                
                string codecName = Marshal.PtrToStringAnsi((IntPtr)_codec->Name) ?? "Unknown";
                LogInfo($"Found software decoder: {codecName}");
                
                _context = FFmpegApi.avcodec_alloc_context3(_codec);
                if (_context == null)
                {
                    throw new OutOfMemoryException("Failed to allocate codec context");
                }
                
                _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                
                int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
                if (openResult < 0)
                {
                    throw new InvalidOperationException($"Failed to open codec: {GetErrorDescription(openResult)}");
                }
                
                LogInfo("Software decoder initialized successfully");
            }
            catch (Exception ex)
            {
                LogError($"Exception in InitializeSoftwareDecoder: {ex.Message}");
                throw;
            }
        }

        private string GetHardwareDecoderName(AVCodecID codecId)
        {
            switch (codecId)
            {
                case AVCodecID.AV_CODEC_ID_H264:
                    return "h264_mediacodec";
                case AVCodecID.AV_CODEC_ID_VP8:
                    return "vp8_mediacodec";
                default:
                    return null;
            }
        }

        private void SetupDecodeFunction()
        {
            try
            {
                int avCodecRawVersion = FFmpegApi.avcodec_version();
                int avCodecMajorVersion = avCodecRawVersion >> 16;
                int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

                LogInfo($"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}");

                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                    LogInfo("Using FFCodec API (libavcodec >= 59.25)");
                }
                else if (avCodecMajorVersion == 59)
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                    LogInfo("Using FFCodecLegacy API (libavcodec 59.x)");
                }
                else
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                    LogInfo("Using FFCodecLegacy API (libavcodec <= 58.x)");
                }
            }
            catch (Exception ex)
            {
                LogError($"Exception in SetupDecodeFunction: {ex.Message}");
                throw;
            }
        }

        static FFmpegContext()
        {
            try
            {
                _logFunc = Log;
                
                FFmpegApi.av_log_set_level(AVLog.MaxOffset);
                FFmpegApi.av_log_set_callback(_logFunc);
                
                LogInfoStatic("FFmpeg logging initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize FFmpeg logging: {ex.Message}");
            }
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
            LogDebug($"DecodeFrame: bitstream length={bitstream.Length}, hardware={_useHardwareAcceleration}");
            
            int result = 0;
            int gotFrame = 0;

            try
            {
                FFmpegApi.av_frame_unref(output.Frame);
                
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    _packet->Flags = 0;
                    
                    LogDebug($"Sending packet: size={bitstream.Length}");
                    
                    result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                }
                
                if (gotFrame == 0 && result >= 0)
                {
                    LogDebug("No frame received, trying to flush delayed frames");
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                    
                    if (_context->HasBFrames != 0)
                    {
                        _context->HasBFrames = 0;
                    }
                }
                
                FFmpegApi.av_packet_unref(_packet);
                
                if (gotFrame == 0)
                {
                    FFmpegApi.av_frame_unref(output.Frame);
                    LogDebug("No frame decoded");
                    return -1;
                }
                
                if (result < 0)
                {
                    LogError($"Decode error: {GetErrorDescription(result)}");
                    FFmpegApi.av_frame_unref(output.Frame);
                    return result;
                }
                
                LogDebug($"Frame decoded successfully: format={output.Frame->Format}, width={output.Frame->Width}, height={output.Frame->Height}");
                
                if (_useHardwareAcceleration && output.Frame->Format == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
                {
                    LogDebug("Hardware frame detected, will need to transfer to software frame");
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogError($"Exception in DecodeFrame: {ex.Message}");
                return -1;
            }
        }

        public void Dispose()
        {
            LogInfo("Disposing FFmpegContext");
            
            try
            {
                if (_packet != null)
                {
                    fixed (AVPacket** ppPacket = &_packet)
                    {
                        FFmpegApi.av_packet_free(ppPacket);
                    }
                }
                
                if (_hw_device_ctx != null)
                {
                    fixed (AVBufferRef** ppBuffer = &_hw_device_ctx)
                    {
                        FFmpegApi.av_buffer_unref(ppBuffer);
                    }
                }
                
                if (_context != null)
                {
                    FFmpegApi.avcodec_close(_context);
                    
                    fixed (AVCodecContext** ppContext = &_context)
                    {
                        FFmpegApi.avcodec_free_context(ppContext);
                    }
                }
                
                LogInfo("FFmpegContext disposed successfully");
            }
            catch (Exception ex)
            {
                LogError($"Exception in Dispose: {ex.Message}");
            }
        }

        private string GetErrorDescription(int errorCode)
        {
            const int bufferSize = 256;
            byte* buffer = stackalloc byte[bufferSize];
            
            int result = FFmpegApi.av_strerror(errorCode, buffer, bufferSize);
            if (result < 0)
            {
                return $"Unknown error: {errorCode}";
            }
            
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error: {errorCode}";
        }

        private void LogInfo(string message)
        {
            if (EnableDebugLogs)
            {
                Debug.WriteLine($"[FFmpegContext] {message}");
            }
            Logger.Info?.Print(LogClass.FFmpeg, message);
        }

        private void LogError(string message)
        {
            if (EnableDebugLogs)
            {
                Debug.WriteLine($"[FFmpegContext] ERROR: {message}");
            }
            Logger.Error?.Print(LogClass.FFmpeg, message);
        }

        private void LogDebug(string message)
        {
            if (EnableDebugLogs)
            {
                Debug.WriteLine($"[FFmpegContext] DEBUG: {message}");
            }
        }

        private static void LogInfoStatic(string message)
        {
            if (EnableDebugLogs)
            {
                Debug.WriteLine($"[FFmpegContext] {message}");
            }
        }

        public static bool IsHardwareDecoderAvailable(AVCodecID codecId)
        {
            try
            {
                string decoderName = GetHardwareDecoderNameStatic(codecId);
                if (decoderName == null)
                    return false;
                    
                AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                return codec != null;
            }
            catch
            {
                return false;
            }
        }

        private static string GetHardwareDecoderNameStatic(AVCodecID codecId)
        {
            switch (codecId)
            {
                case AVCodecID.AV_CODEC_ID_H264:
                    return "h264_mediacodec";
                case AVCodecID.AV_CODEC_ID_VP8:
                    return "vp8_mediacodec";
                case AVCodecID.AV_CODEC_ID_VP9:
                    return "vp9_mediacodec";
                case AVCodecID.AV_CODEC_ID_HEVC:
                case AVCodecID.AV_CODEC_ID_H265:
                    return "hevc_mediacodec";
                default:
                    return null;
            }
        }

        public string GetDecoderType()
        {
            return _useHardwareAcceleration ? "Hardware" : "Software";
        }
    }
}
