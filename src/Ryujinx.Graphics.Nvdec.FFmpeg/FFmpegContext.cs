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
        private bool _initialized = false;
        
        private static readonly bool EnableDebugLogs = true;
        
        // ANativeWindow 指针
        private long _nativeWindowPtr = -1;
        
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
            
            try
            {
                // 先初始化软件解码器作为后备
                InitializeSoftwareDecoder(codecId);
                
                // 如果有NativeWindow，尝试硬件解码
                if (_nativeWindowPtr != -1 && TryInitializeHardwareDecoder(codecId))
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
                }
                
                _packet = FFmpegApi.av_packet_alloc();
                if (_packet == null)
                {
                    LogError("Failed to allocate packet");
                    throw new OutOfMemoryException("Failed to allocate AVPacket");
                }
                
                _decodeFrame = SetupDecodeFunction();
                
                _initialized = true;
                LogInfo($"FFmpegContext initialized successfully: HardwareAcceleration={_useHardwareAcceleration}, Decoder={GetDecoderName()}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to initialize FFmpegContext: {ex.Message}");
                
                // 清理资源
                Cleanup();
                throw;
            }
        }

        // 备用构造函数（不使用 NativeWindow）
        public FFmpegContext(AVCodecID codecId) : this(codecId, -1)
        {
        }

        private bool TryInitializeHardwareDecoder(AVCodecID codecId)
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
                
                // 创建硬件设备上下文
                if (!CreateHardwareDeviceContext())
                {
                    LogInfo("Failed to create hardware device context");
                    return false;
                }
                
                AVCodecContext* hwContext = FFmpegApi.avcodec_alloc_context3(hwCodec);
                if (hwContext == null)
                {
                    LogError("Failed to allocate hardware codec context");
                    return false;
                }
                
                // 设置硬件设备上下文
                hwContext->HwDeviceCtx = FFmpegApi.av_buffer_ref(_hw_device_ctx);
                if (hwContext->HwDeviceCtx == null)
                {
                    LogError("Failed to set hardware device context");
                    
                    AVCodecContext* tempContext = hwContext;
                    FFmpegApi.avcodec_free_context(&tempContext);
                    return false;
                }
                
                // 设置像素格式为 MediaCodec
                hwContext->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
                
                // 设置硬件解码器参数
                SetupHardwareDecoderParameters(hwContext);
                
                // 打开硬件解码器
                int openResult = FFmpegApi.avcodec_open2(hwContext, hwCodec, null);
                if (openResult < 0)
                {
                    LogError($"Failed to open hardware codec: {GetErrorDescription(openResult)}");
                    
                    _hardwareFailureCount++;
                    
                    // 清理硬件上下文
                    AVCodecContext* tempContext = hwContext;
                    FFmpegApi.avcodec_free_context(&tempContext);
                    
                    // 清理硬件设备上下文
                    if (_hw_device_ctx != null)
                    {
                        AVBufferRef* tempDeviceCtx = _hw_device_ctx;
                        FFmpegApi.av_buffer_unref(&tempDeviceCtx);
                        _hw_device_ctx = null;
                    }
                    
                    if (_hardwareFailureCount > 3)
                    {
                        LogWarning($"Hardware decoder failed {_hardwareFailureCount} times, disabling hardware decoding");
                    }
                    
                    return false;
                }
                
                // 硬件解码器初始化成功，现在替换当前的软件解码器
                // 首先清理软件解码器
                if (_context != null)
                {
                    FFmpegApi.avcodec_close(_context);
                    
                    AVCodecContext* tempContext = _context;
                    FFmpegApi.avcodec_free_context(&tempContext);
                    _context = null;
                }
                
                if (_codec != null)
                {
                    _codec = null;
                }
                
                // 设置硬件解码器
                _context = hwContext;
                _codec = hwCodec;
                
                // 重新设置解码函数（硬件解码可能需要不同的解码函数）
                _decodeFrame = SetupDecodeFunction();
                
                LogInfo($"Hardware decoder opened successfully");
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Exception in TryInitializeHardwareDecoder: {ex.Message}");
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
                        if (device_ref != null)
                        {
                            FFmpegApi.av_buffer_unref(&device_ref);
                        }
                        return false;
                    }
                }
                
                _hw_device_ctx = device_ref;
                
                result = FFmpegApi.av_hwdevice_ctx_init(_hw_device_ctx);
                if (result < 0)
                {
                    LogError($"av_hwdevice_ctx_init failed: {GetErrorDescription(result)}");
                    FFmpegApi.av_buffer_unref(&device_ref);
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

        private void SetupHardwareDecoderParameters(AVCodecContext* ctx)
        {
            try
            {
                // 设置硬件解码器参数
                ctx->Flags |= FFmpegApi.AV_CODEC_FLAG_HWACCEL;
                
                // 设置低延迟模式
                ctx->Flags |= FFmpegApi.AV_CODEC_FLAG_LOW_DELAY;
                
                // 设置线程数
                ctx->ThreadCount = 1; // 硬件解码通常使用单线程
                
                // 设置 B 帧处理
                ctx->HasBFrames = 0;
                
                // 设置错误恢复
                ctx->ErrRecognition = FFmpegApi.AV_EF_EXPLODE;
                
                LogInfo("Hardware decoder parameters configured");
            }
            catch (Exception ex)
            {
                LogError($"Error setting hardware decoder parameters: {ex.Message}");
            }
        }

        private void SetupSoftwareDecoderParameters(AVCodecContext* ctx)
        {
            try
            {
                // 设置软件解码器参数
                ctx->Flags |= FFmpegApi.AV_CODEC_FLAG_LOW_DELAY;
                
                // 设置线程数
                ctx->ThreadCount = 2; // 软件解码可以使用多线程
                
                // 设置 B 帧处理
                ctx->HasBFrames = 0;
                
                // 设置错误恢复
                ctx->ErrRecognition = FFmpegApi.AV_EF_EXPLODE;
                
                LogInfo("Software decoder parameters configured");
            }
            catch (Exception ex)
            {
                LogError($"Error setting software decoder parameters: {ex.Message}");
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
                
                SetupSoftwareDecoderParameters(_context);
                
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
                if (_codec == null)
                {
                    throw new InvalidOperationException("Codec not initialized");
                }
                
                int avCodecRawVersion = FFmpegApi.avcodec_version();
                int avCodecMajorVersion = avCodecRawVersion >> 16;
                int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

                LogInfo($"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}");
                LogInfo($"Decoder type: {(_useHardwareAcceleration ? "Hardware" : "Software")}");
                LogInfo($"Decoder name: {Marshal.PtrToStringAnsi((IntPtr)_codec->Name) ?? "Unknown"}");

                // 使用安全的方式获取解码函数指针
                IntPtr decodeFuncPtr = IntPtr.Zero;
                
                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    var ffcodec = (FFCodec<AVCodec>*)_codec;
                    if (ffcodec != null && ffcodec->CodecCallback != IntPtr.Zero)
                    {
                        decodeFuncPtr = ffcodec->CodecCallback;
                        LogInfo("Using FFCodec API (libavcodec >= 59.25)");
                    }
                }
                else if (avCodecMajorVersion == 59)
                {
                    var ffcodec = (FFCodecLegacy<AVCodec501>*)_codec;
                    if (ffcodec != null && ffcodec->Decode != IntPtr.Zero)
                    {
                        decodeFuncPtr = ffcodec->Decode;
                        LogInfo("Using FFCodecLegacy API (libavcodec 59.x)");
                    }
                }
                else
                {
                    var ffcodec = (FFCodecLegacy<AVCodec>*)_codec;
                    if (ffcodec != null && ffcodec->Decode != IntPtr.Zero)
                    {
                        decodeFuncPtr = ffcodec->Decode;
                        LogInfo("Using FFCodecLegacy API (libavcodec <= 58.x)");
                    }
                }
                
                if (decodeFuncPtr == IntPtr.Zero)
                {
                    LogWarning("Failed to get decode function pointer, using fallback");
                    // 使用简单的解码函数作为后备
                    return SimpleDecodeFallback;
                }
                
                LogInfo($"Decode function pointer obtained: 0x{decodeFuncPtr:X}");
                return Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(decodeFuncPtr);
            }
            catch (Exception ex)
            {
                LogError($"Exception in SetupDecodeFunction: {ex.Message}");
                // 返回后备解码函数
                return SimpleDecodeFallback;
            }
        }
        
        // 简单的解码后备函数
        private int SimpleDecodeFallback(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt)
        {
            // 这是一个简单的后备解码函数，实际使用时应该实现具体的解码逻辑
            // 这里返回一个错误，表示不支持
            return -1;
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
            if (!_initialized)
            {
                LogError("FFmpegContext not initialized");
                return -1;
            }
            
            LogDebug($"DecodeFrame: bitstream length={bitstream.Length}, hardware={_useHardwareAcceleration}");
            
            _decodeTimer.Restart();
            _frameCount++;
            
            int result = 0;
            int gotFrame = 0;

            try
            {
                if (_context == null)
                {
                    LogError("Codec context is null");
                    return -1;
                }
                
                // 修复这里：将 != 改为 ==
                if (_packet == null)  // 正确检查包是否为null
                {
                    LogError("Packet is null");
                    return -1;
                }
                
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
                    if (_packet != null)
                    {
                        _packet->Data = null;
                        _packet->Size = 0;
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

        private void Cleanup()
        {
            try
            {
                if (_packet != null)
                {
                    AVPacket* tempPacket = _packet;
                    FFmpegApi.av_packet_free(&tempPacket);
                    _packet = null;
                }
                
                if (_hw_device_ctx != null)
                {
                    AVBufferRef* tempDeviceCtx = _hw_device_ctx;
                    FFmpegApi.av_buffer_unref(&tempDeviceCtx);
                    _hw_device_ctx = null;
                }
                
                if (_context != null)
                {
                    FFmpegApi.avcodec_close(_context);
                    
                    // 修复：创建临时变量
                    AVCodecContext* tempContext = _context;
                    FFmpegApi.avcodec_free_context(&tempContext);
                    _context = null;
                }
                
                _codec = null;
                _initialized = false;
            }
            catch (Exception ex)
            {
                LogError($"Exception in Cleanup: {ex.Message}");
            }
        }

        public void Dispose()
        {
            LogInfo("Disposing FFmpegContext");
            Cleanup();
            LogInfo($"FFmpegContext disposed successfully. Statistics: Frames={_frameCount}, HWFailures={_hardwareFailureCount}");
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
        
        public bool IsInitialized => _initialized;
    }
}
