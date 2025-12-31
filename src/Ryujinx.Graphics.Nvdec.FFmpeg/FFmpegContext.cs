using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Collections.Generic;

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
        
        // ANativeWindow 指针
        private long _nativeWindowPtr = -1;
        private bool _ownsNativeWindow = false;
        
        // 解码器统计信息
        private int _frameCount = 0;
        private int _hardwareFailureCount = 0;
        private readonly Stopwatch _decodeTimer = new Stopwatch();
        
        // 支持的硬件解码器列表
        private static readonly Dictionary<AVCodecID, string[]> HardwareDecoderMap = new Dictionary<AVCodecID, string[]>
        {
            { AVCodecID.AV_CODEC_ID_H264, new string[] { "h264_mediacodec", "h264_android_mediacodec", "h264" } },
            { AVCodecID.AV_CODEC_ID_VP8, new string[] { "vp8_mediacodec", "vp8_android_mediacodec", "vp8" } },
        };

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

        // 主构造函数
        public FFmpegContext(AVCodecID codecId, long nativeWindowPtr = -1)
        {
            LogInfo($"Initializing FFmpegContext for codec: {codecId}");
            
            _nativeWindowPtr = nativeWindowPtr;
            _ownsNativeWindow = false; // NativeWindow 由 Java 层管理
            
            try
            {
                // 尝试硬件解码
                if (_nativeWindowPtr != -1 && InitializeHardwareDecoder(codecId))
                {
                    _useHardwareAcceleration = true;
                    LogInfo($"Hardware decoder initialized successfully with NativeWindow: 0x{_nativeWindowPtr:X}");
                }
                else
                {
                    if (_nativeWindowPtr == -1)
                    {
                        LogInfo("No NativeWindow provided, using software decoder");
                    }
                    else
                    {
                        LogInfo($"Hardware decoder initialization failed (NativeWindow: 0x{_nativeWindowPtr:X}), using software decoder");
                    }
                    
                    InitializeSoftwareDecoder(codecId);
                }
                
                _packet = FFmpegApi.av_packet_alloc();
                if (_packet == null)
                {
                    LogError("Failed to allocate packet");
                    throw new OutOfMemoryException("Failed to allocate AVPacket");
                }
                
                _decodeFrame = SetupDecodeFunction();
                
                LogInfo($"FFmpegContext initialized successfully: HardwareAcceleration={_useHardwareAcceleration}, Decoder={GetDecoderName()}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize FFmpegContext: {ex.Message}");
                throw;
            }
        }

        // 备用构造函数（不使用 NativeWindow）
        public FFmpegContext(AVCodecID codecId) : this(codecId, -1)
        {
        }

        private bool InitializeHardwareDecoder(AVCodecID codecId)
        {
            try
            {
                LogInfo("Attempting to initialize hardware decoder");
                
                string hardwareDecoderName = FindHardwareDecoder(codecId);
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
                
                // 设置硬件设备上下文
                _context->HwDeviceCtx = FFmpegApi.av_buffer_ref(_hw_device_ctx);
                if (_context->HwDeviceCtx == null)
                {
                    LogError("Failed to set hardware device context");
                    return false;
                }
                
                // 设置像素格式为 MediaCodec
                _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
                
                // 设置解码器参数
                SetupDecoderParameters();
                
                // 打开解码器
                int openResult = FFmpegApi.avcodec_open2(_context, hwCodec, null);
                if (openResult < 0)
                {
                    LogError($"Failed to open hardware codec: {GetErrorDescription(openResult)}");
                    
                    // 记录硬件解码失败次数
                    _hardwareFailureCount++;
                    
                    // 如果硬件解码失败次数太多，可能禁用硬件解码
                    if (_hardwareFailureCount > 3)
                    {
                        LogWarning($"Hardware decoder failed {_hardwareFailureCount} times, considering disabling it");
                    }
                    
                    return false;
                }
                
                _codec = hwCodec;
                
                LogInfo($"Hardware decoder opened successfully: {codecName}");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Exception in InitializeHardwareDecoder: {ex.Message}");
                _hardwareFailureCount++;
                return false;
            }
        }

        private string FindHardwareDecoder(AVCodecID codecId)
        {
            if (!HardwareDecoderMap.TryGetValue(codecId, out var decoderNames))
            {
                return null;
            }
            
            foreach (var decoderName in decoderNames)
            {
                AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                if (codec != null)
                {
                    LogInfo($"Found hardware decoder candidate: {decoderName}");
                    return decoderName;
                }
            }
            
            return null;
        }

        private bool CreateHardwareDeviceContext()
        {
            try
            {
                LogInfo("Creating MediaCodec hardware device context");
                
                AVDictionary* opts = null;
                
                // 设置创建窗口选项
                int result = FFmpegApi.av_dict_set(&opts, "create_window", "1", 0);
                if (result < 0)
                {
                    LogWarning("Failed to set create_window option");
                }
                
                // 添加延迟刷新选项
                result = FFmpegApi.av_dict_set(&opts, "delay_flush", "1", 0);
                if (result < 0)
                {
                    LogWarning("Failed to set delay_flush option");
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
                    
                    // 尝试不使用选项
                    LogInfo("Retrying without options");
                    result = FFmpegApi.av_hwdevice_ctx_create(&device_ref,
                        FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC,
                        null, null, 0);
                    
                    if (result < 0)
                    {
                        LogError($"Second attempt failed: {GetErrorDescription(result)}");
                        return false;
                    }
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

        private void SetupDecoderParameters()
        {
            try
            {
                // 设置解码器参数
                _context->Flags |= FFmpegApi.AV_CODEC_FLAG_HWACCEL;
                
                // 设置低延迟模式
                _context->Flags |= FFmpegApi.AV_CODEC_FLAG_LOW_DELAY;
                
                // 设置线程数
                _context->ThreadCount = 2;
                
                // 设置 B 帧处理
                _context->HasBFrames = 0;
                
                // 设置错误恢复
                _context->ErrRecognition = FFmpegApi.AV_EF_EXPLODE;
                
                LogInfo("Decoder parameters configured");
            }
            catch (Exception ex)
            {
                LogError($"Error setting decoder parameters: {ex.Message}");
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
                
                SetupDecoderParameters();
                
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

        private AVCodec_decode SetupDecodeFunction()
        {
            try
            {
                int avCodecRawVersion = FFmpegApi.avcodec_version();
                int avCodecMajorVersion = avCodecRawVersion >> 16;
                int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

                LogInfo($"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}");

                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    var decodeFunc = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                    LogInfo("Using FFCodec API (libavcodec >= 59.25)");
                    return decodeFunc;
                }
                else if (avCodecMajorVersion == 59)
                {
                    var decodeFunc = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                    LogInfo("Using FFCodecLegacy API (libavcodec 59.x)");
                    return decodeFunc;
                }
                else
                {
                    var decodeFunc = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                    LogInfo("Using FFCodecLegacy API (libavcodec <= 58.x)");
                    return decodeFunc;
                }
            }
            catch (Exception ex)
            {
                LogError($"Exception in SetupDecodeFunction: {ex.Message}");
                throw;
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
            
            _decodeTimer.Restart();
            _frameCount++;
            
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
                    _decodeTimer.Stop();
                    return -1;
                }
                
                if (result < 0)
                {
                    LogError($"Decode error: {GetErrorDescription(result)}");
                    FFmpegApi.av_frame_unref(output.Frame);
                    _decodeTimer.Stop();
                    return result;
                }
                
                _decodeTimer.Stop();
                
                LogDebug($"Frame decoded successfully: format={output.Frame->Format}, width={output.Frame->Width}, height={output.Frame->Height}, decode_time={_decodeTimer.ElapsedMilliseconds}ms");
                
                // 如果是硬件解码，可能需要特殊处理
                if (_useHardwareAcceleration && output.Frame->Format == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
                {
                    LogDebug("Hardware frame detected with MediaCodec pixel format");
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogError($"Exception in DecodeFrame: {ex.Message}");
                _decodeTimer.Stop();
                return -1;
            }
        }

        public void Flush()
        {
            try
            {
                LogInfo("Flushing decoder buffers");
                
                if (_context != null)
                {
                    // 发送空包以刷新解码器
                    _packet->Data = null;
                    _packet->Size = 0;
                    
                    int gotFrame = 0;
                    AVFrame* frame = FFmpegApi.av_frame_alloc();
                    
                    if (frame != null)
                    {
                        while (true)
                        {
                            int result = _decodeFrame(_context, frame, &gotFrame, _packet);
                            if (result < 0 || gotFrame == 0)
                            {
                                break;
                            }
                            
                            FFmpegApi.av_frame_unref(frame);
                        }
                        
                        FFmpegApi.av_frame_free(&frame);
                    }
                    
                    // 使用 avcodec_flush_buffers 刷新缓冲区
                    FFmpegApi.avcodec_flush_buffers(_context);
                    
                    LogInfo("Decoder buffers flushed successfully");
                }
            }
            catch (Exception ex)
            {
                LogError($"Exception in Flush: {ex.Message}");
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
                    _packet = null;
                }
                
                if (_hw_device_ctx != null)
                {
                    fixed (AVBufferRef** ppBuffer = &_hw_device_ctx)
                    {
                        FFmpegApi.av_buffer_unref(ppBuffer);
                    }
                    _hw_device_ctx = null;
                }
                
                // 释放 NativeWindow（如果由我们管理）
                if (_ownsNativeWindow && _nativeWindowPtr != -1)
                {
                    // 这里需要调用适当的函数来释放 NativeWindow
                    // AndroidJni.ReleaseNativeWindow(_nativeWindowPtr);
                    _nativeWindowPtr = -1;
                }
                
                if (_context != null)
                {
                    FFmpegApi.avcodec_close(_context);
                    
                    fixed (AVCodecContext** ppContext = &_context)
                    {
                        FFmpegApi.avcodec_free_context(ppContext);
                    }
                    _context = null;
                }
                
                LogInfo($"FFmpegContext disposed successfully. Statistics: Frames={_frameCount}, HWFailures={_hardwareFailureCount}");
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
                return $"Unknown error: {errorCode} (0x{errorCode:X})";
            }
            
            return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error: {errorCode} (0x{errorCode:X})";
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

        private void LogWarning(string message)
        {
            if (EnableDebugLogs)
            {
                Debug.WriteLine($"[FFmpegContext] WARNING: {message}");
            }
            Logger.Warning?.Print(LogClass.FFmpeg, message);
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
                // 检查是否存在硬件解码器
                if (!HardwareDecoderMap.TryGetValue(codecId, out var decoderNames))
                {
                    return false;
                }
                
                foreach (var decoderName in decoderNames)
                {
                    AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                    if (codec != null)
                    {
                        LogInfoStatic($"Hardware decoder available: {decoderName}");
                        return true;
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                LogInfoStatic($"Error checking hardware decoder availability: {ex.Message}");
                return false;
            }
        }

        public string GetDecoderName()
        {
            if (_codec == null)
                return "Unknown";
            
            string name = Marshal.PtrToStringAnsi((IntPtr)_codec->Name) ?? "Unknown";
            return $"{name} ({(_useHardwareAcceleration ? "Hardware" : "Software")})";
        }

        public string GetDecoderType()
        {
            return _useHardwareAcceleration ? "Hardware" : "Software";
        }

        public bool IsHardwareAccelerated => _useHardwareAcceleration;

        public long NativeWindowPtr => _nativeWindowPtr;

        public int FrameCount => _frameCount;

        public int HardwareFailureCount => _hardwareFailureCount;

        public TimeSpan AverageDecodeTime => _frameCount > 0 ? TimeSpan.FromMilliseconds(_decodeTimer.ElapsedMilliseconds / _frameCount) : TimeSpan.Zero;

        public void ResetStatistics()
        {
            _frameCount = 0;
            _hardwareFailureCount = 0;
            _decodeTimer.Reset();
        }
    }
}
