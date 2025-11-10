using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// FFmpeg 解码器上下文（软件解码和 FFmpeg 内置硬件解码）
    /// </summary>
    unsafe class FFmpegContext : IVideoDecoder, IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);

        private AVCodec_decode _decodeFrame;
        private static FFmpegApi.av_log_set_callback_callback _logFunc; // 移除 readonly 修饰符
        private AVCodec* _codec;
        private AVPacket* _packet;
        private AVCodecContext* _context;
        private bool _useNewApi;
        private bool _isFirstFrame = true;
        private bool _needsFlush = false;
        private System.Diagnostics.Stopwatch _decodeTimer = new System.Diagnostics.Stopwatch();
        private int _frameCount = 0;
        private bool _useHardwareDecoder;
        private string _decoderType;
        private string _hardwareDecoderName;
        private AVBufferRef* _hwDeviceContext;
        private AVPixelFormat _hwPixelFormat;
        private bool _isInitialized = false;
        private List<AVCodecHWConfig> _hwConfigs;

        // get_format 回调委托
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate AVPixelFormat GetFormatDelegate(AVCodecContext* s, AVPixelFormat* pix_fmts);

        private GetFormatDelegate _getFormatCallback;
        private IntPtr _getFormatCallbackPtr;

        // Android 硬件解码器映射
        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_HEVC, new[] { "hevc_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP9, new[] { "vp9_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_AV1, new[] { "av1_mediacodec" } },
        };

        // 实现 IVideoDecoder 接口
        public bool IsInitialized => _isInitialized;
        public bool IsHardwareDecoder => _useHardwareDecoder;
        public string CodecName => GetCodecName(_codec);
        public string HardwareDecoderName => _hardwareDecoderName ?? "None";

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="codecId">编解码器 ID</param>
        /// <param name="preferHardware">是否优先使用硬件解码</param>
        public FFmpegContext(AVCodecID codecId, bool preferHardware = true)
        {
            Logger.Info?.Print(LogClass.FFmpeg, $"Initializing FFmpeg decoder for {codecId}, Hardware preference: {preferHardware}");

            // 检查是否应该使用新的硬件解码器
            if (preferHardware && ShouldUseNativeHardwareDecoder(codecId))
            {
                Logger.Info?.Print(LogClass.FFmpeg, $"Using native hardware decoder for {codecId}");
                _isInitialized = false; // 标记为未初始化，让工厂类使用 FFmpegHardwareDecoder
                return;
            }

            InitializeSoftwareDecoder(codecId, preferHardware);
        }

        /// <summary>
        /// 判断是否应该使用原生硬件解码器
        /// </summary>
        private bool ShouldUseNativeHardwareDecoder(AVCodecID codecId)
        {
            try
            {
                // 检查硬件解码器是否可用
                if (!FFmpegHardwareDecoder.IsMediaCodecSupported())
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "MediaCodec not supported, using software decoder");
                    return false;
                }

                string codecName = GetCodecNameFromId(codecId);
                if (!FFmpegHardwareDecoder.IsCodecHardwareSupported(codecName))
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder not available for {codecName}, using software decoder");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Error checking hardware decoder support: {ex.Message}, falling back to software");
                return false;
            }
        }

        /// <summary>
        /// 初始化软件解码器
        /// </summary>
        private void InitializeSoftwareDecoder(AVCodecID codecId, bool preferHardware)
        {
            // 设置 get_format 回调
            _getFormatCallback = GetHwFormat;
            _getFormatCallbackPtr = Marshal.GetFunctionPointerForDelegate(_getFormatCallback);

            // 直接初始化字段
            string hardwareDecoderName = null;
            bool useHardwareDecoder = false;
            AVCodec* codec = null;

            // 尝试硬件解码器（如果启用且可用）
            if (preferHardware)
            {
                if (AndroidHardwareDecoders.TryGetValue(codecId, out string[] decoderNames))
                {
                    foreach (string decoderName in decoderNames)
                    {
                        codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                        if (codec != null)
                        {
                            hardwareDecoderName = decoderName;
                            useHardwareDecoder = true;
                            Logger.Debug?.Print(LogClass.FFmpeg, $"Found hardware decoder: {decoderName}");
                            break;
                        }
                        else
                        {
                            Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder not available: {decoderName}");
                        }
                    }
                }

                if (codec != null)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected hardware decoder: {hardwareDecoderName}");
                }
                else
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, $"No compatible hardware decoder found for {codecId}");
                }
            }

            // 如果硬件解码器不可用，回退到软件解码器
            if (codec == null)
            {
                codec = FFmpegApi.avcodec_find_decoder(codecId);
                useHardwareDecoder = false;
                
                if (codec != null)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected software decoder: {GetCodecName(codec)}");
                }
            }

            if (codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found for {codecId}. Make sure you have the required codec present in your FFmpeg installation.");
                return;
            }

            // 设置字段
            _codec = codec;
            _useHardwareDecoder = useHardwareDecoder;
            _decoderType = useHardwareDecoder ? "Hardware" : "Software";
            _hardwareDecoderName = hardwareDecoderName;

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 关键修复：先进行基础配置，再尝试硬件解码
            if (!ConfigureBaseDecoderContext())
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to configure base decoder context.");
                return;
            }

            // 如果是硬件解码，尝试配置硬件解码器
            if (_useHardwareDecoder)
            {
                if (!ConfigureHardwareDecoder())
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decoder configuration failed, falling back to software decoder");
                    if (!FallbackToSoftwareDecoder())
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to fallback to software decoder.");
                        return;
                    }
                }
            }

            // 打开编解码器
            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            if (openResult != 0)
            {
                string errorMsg = GetFFmpegErrorString(openResult);
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened (Error: {openResult}, {errorMsg}).");
                
                // 如果是硬件解码失败，尝试回退到软件解码
                if (_useHardwareDecoder)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "Hardware decoder failed to open, falling back to software decoder");
                    
                    if (!ReinitializeAsSoftwareDecoder(codecId))
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to reinitialize as software decoder");
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            // 检测 API 版本
            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}, using {(avCodecMajorVersion >= 58 ? "new" : "old")} API");

            // 检测是否使用新版 API (avcodec_send_packet/avcodec_receive_frame)
            _useNewApi = avCodecMajorVersion >= 58;

            if (!_useNewApi)
            {
                // 旧版 API 路径
                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                }
                else if (avCodecMajorVersion == 59)
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                }
                else
                {
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                }
            }

            _isInitialized = true;
            Logger.Info?.Print(LogClass.FFmpeg, $"FFmpeg {_decoderType} decoder initialized successfully (API: {(_useNewApi ? "New" : "Old")}, Codec: {GetCodecName(_codec)})");
        }

        private bool ConfigureBaseDecoderContext()
        {
            try
            {
                Logger.Debug?.Print(LogClass.FFmpeg, "Configuring base decoder context...");

                // 基础解码器配置 - 适用于所有解码器
                _context->ErrRecognition = 0x0001; // 基础错误识别
                _context->ErrorConcealment = 0x0001 | 0x0002; // 帧和边界错误隐藏
                _context->WorkaroundBugs = 1; // 启用bug规避
                
                // 对于 H.264 解码器，设置一些常用参数
                if (_codec->Id == AVCodecID.AV_CODEC_ID_H264)
                {
                    _context->Flags2 |= 0x00000001; // CODEC_FLAG2_FAST
                    _context->Refs = 3; // 参考帧数量
                }

                // 设置线程数
                _context->ThreadCount = Math.Min(Environment.ProcessorCount, 4);
                
                Logger.Debug?.Print(LogClass.FFmpeg, "Base decoder context configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Exception configuring base decoder context: {ex.Message}");
                return false;
            }
        }

        private bool ConfigureHardwareDecoder()
        {
            try
            {
                Logger.Debug?.Print(LogClass.FFmpeg, "Configuring hardware decoder...");

                // 创建硬件设备上下文
                AVHWDeviceType deviceType = FFmpegApi.av_hwdevice_find_type_by_name("mediacodec");
                if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "MediaCodec hardware device type not supported");
                    return false;
                }

                Logger.Debug?.Print(LogClass.FFmpeg, $"Found MediaCodec device type: {deviceType}");

                // 获取硬件配置列表
                _hwConfigs = GetHardwareConfigs(_codec, deviceType);
                if (_hwConfigs.Count == 0)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "No compatible hardware configurations found");
                    return false;
                }

                // 创建硬件设备上下文
                int result;
                fixed (AVBufferRef** ppHwDeviceContext = &_hwDeviceContext)
                {
                    result = FFmpegApi.av_hwdevice_ctx_create(ppHwDeviceContext, deviceType, null, null, 0);
                }
                
                if (result < 0)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, $"Failed to create hardware device context: {result}");
                    return false;
                }

                Logger.Debug?.Print(LogClass.FFmpeg, "Hardware device context created successfully");

                // 设置硬件设备上下文到编解码器上下文
                _context->HwDeviceCtx = (nint)FFmpegApi.av_buffer_ref(_hwDeviceContext);
                if (_context->HwDeviceCtx == IntPtr.Zero)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, "Failed to set hardware device context");
                    return false;
                }

                // 关键修复：设置 get_format 回调，让硬件解码器自己协商像素格式
                _context->GetFormat = _getFormatCallbackPtr;

                // 存储硬件配置信息用于回调
                _context->Opaque = (nint)GCHandle.Alloc(_hwConfigs, GCHandleType.Normal);

                // 硬件解码器特定优化
                _context->ThreadCount = 1; // 硬件解码器通常单线程性能更好
                
                Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Exception configuring hardware decoder: {ex.Message}");
                return false;
            }
        }

        private List<AVCodecHWConfig> GetHardwareConfigs(AVCodec* codec, AVHWDeviceType deviceType)
        {
            var configs = new List<AVCodecHWConfig>();
            
            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(codec, i);
                if (config == null) break;

                Logger.Debug?.Print(LogClass.FFmpeg, 
                    $"HW Config {i}: device_type={config->device_type}, pix_fmt={config->pix_fmt}, methods={config->methods}");

                if ((config->methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                    config->device_type == deviceType)
                {
                    configs.Add(*config);
                }
            }

            return configs;
        }

        private bool FallbackToSoftwareDecoder()
        {
            // 回退到软件解码器而不重新创建编解码器上下文
            _useHardwareDecoder = false;
            _decoderType = "Software";
            _hardwareDecoderName = null;
            
            // 清理硬件资源
            CleanupHardwareResources();
            
            // 重新配置线程数
            _context->ThreadCount = Math.Min(Environment.ProcessorCount, 4);
            
            Logger.Info?.Print(LogClass.FFmpeg, "Falling back to software decoder");
            return true;
        }

        private bool ReinitializeAsSoftwareDecoder(AVCodecID codecId)
        {
            Logger.Info?.Print(LogClass.FFmpeg, "Reinitializing as software decoder...");

            try
            {
                // 清理现有资源
                CleanupHardwareResources();
                
                // 清理 Opaque 数据
                if (_context != null && _context->Opaque != IntPtr.Zero)
                {
                    var handle = GCHandle.FromIntPtr(_context->Opaque);
                    if (handle.IsAllocated)
                    {
                        handle.Free();
                    }
                }

                // 释放现有的 packet
                if (_packet != null)
                {
                    fixed (AVPacket** ppPacket = &_packet)
                    {
                        FFmpegApi.av_packet_free(ppPacket);
                    }
                    _packet = null; // 设置为 null，稍后重新分配
                }

                if (_context != null)
                {
                    fixed (AVCodecContext** ppContext = &_context)
                    {
                        FFmpegApi.avcodec_free_context(ppContext);
                    }
                    _context = null;
                }

                // 重新创建软件解码器
                _useHardwareDecoder = false;
                _decoderType = "Software";
                _hardwareDecoderName = null;
                
                // 查找软件解码器
                AVCodec* softwareCodec = FFmpegApi.avcodec_find_decoder(codecId);
                if (softwareCodec == null)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, "Software decoder not found during fallback");
                    return false;
                }

                _codec = softwareCodec;

                _context = FFmpegApi.avcodec_alloc_context3(_codec);
                if (_context == null)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, "Failed to allocate software codec context during fallback");
                    return false;
                }

                // 配置基础解码器
                if (!ConfigureBaseDecoderContext())
                {
                    return false;
                }

                int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
                if (openResult != 0)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, $"Software decoder also failed to open: {openResult}");
                    return false;
                }

                // 重新分配 packet
                _packet = FFmpegApi.av_packet_alloc();
                if (_packet == null)
                {
                    Logger.Error?.Print(LogClass.FFmpeg, "Packet couldn't be allocated during fallback.");
                    return false;
                }

                _isInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, $"Error during software decoder reinitialization: {ex.Message}");
                return false;
            }
        }

        private void CleanupHardwareResources()
        {
            if (_hwDeviceContext != null)
            {
                fixed (AVBufferRef** ppHwDeviceContext = &_hwDeviceContext)
                {
                    FFmpegApi.av_buffer_unref(ppHwDeviceContext);
                }
                _hwDeviceContext = null;
            }
        }

        // get_format 回调函数 - 基于 C++ 示例修改
        private AVPixelFormat GetHwFormat(AVCodecContext* ctx, AVPixelFormat* pix_fmts)
        {
            Logger.Debug?.Print(LogClass.FFmpeg, "get_format callback called");

            // 从 Opaque 获取硬件配置
            if (ctx->Opaque != IntPtr.Zero)
            {
                var handle = GCHandle.FromIntPtr(ctx->Opaque);
                if (handle.Target is List<AVCodecHWConfig> hwConfigs && hwConfigs.Count > 0)
                {
                    // 使用第一个兼容的硬件配置，就像 C++ 示例中那样
                    var hwConfig = hwConfigs[0];
                    Logger.Info?.Print(LogClass.FFmpeg, $"Using hardware pixel format from config: {hwConfig.pix_fmt}");
                    return (AVPixelFormat)hwConfig.pix_fmt;
                }
            }

            // 回退逻辑：遍历支持的格式
            for (AVPixelFormat* p = pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                Logger.Debug?.Print(LogClass.FFmpeg, $"Checking pixel format in callback: {*p}");
                
                // 优先选择已知的硬件格式
                if (*p == AVPixelFormat.AV_PIX_FMT_MEDIACODEC || 
                    *p == AVPixelFormat.AV_PIX_FMT_NV12 ||
                    *p == AVPixelFormat.AV_PIX_FMT_YUV420P)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, $"Selected pixel format in callback: {*p}");
                    return *p;
                }
            }

            Logger.Warning?.Print(LogClass.FFmpeg, "No suitable hardware format found, using default");
            return AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        private string GetFFmpegErrorString(int errorCode)
        {
            switch (errorCode)
            {
                case -22: return "EINVAL - Invalid argument";
                case -2: return "ENOENT - No such file or directory"; 
                case -5: return "EIO - I/O error";
                case -12: return "ENOMEM - Out of memory";
                case -1094995529: return "AVERROR_INVALIDDATA - Invalid data found";
                case -541478725: return "AVERROR_EOF - End of file";
                default: return $"Unknown error ({errorCode})";
            }
        }

        private string GetCodecName(AVCodec* codec)
        {
            if (codec == null) return "Unknown";
            return Marshal.PtrToStringAnsi((IntPtr)codec->Name) ?? "Unknown";
        }

        /// <summary>
        /// 根据 AVCodecID 获取编解码器名称
        /// </summary>
        private string GetCodecNameFromId(AVCodecID codecId)
        {
            switch (codecId)
            {
                case AVCodecID.AV_CODEC_ID_H264: return "h264";
                case AVCodecID.AV_CODEC_ID_HEVC: return "hevc";
                case AVCodecID.AV_CODEC_ID_VP8: return "vp8";
                case AVCodecID.AV_CODEC_ID_VP9: return "vp9";
                case AVCodecID.AV_CODEC_ID_AV1: return "av1";
                default: return codecId.ToString().ToLower();
            }
        }

        static FFmpegContext()
        {
            _logFunc = Log;
            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(_logFunc);
        }

        private static void Log(void* ptr, AVLog level, string format, byte* vl)
        {
            if (level > FFmpegApi.av_log_get_level()) return;

            int lineSize = 1024;
            byte* lineBuffer = stackalloc byte[lineSize];
            int printPrefix = 1;

            FFmpegApi.av_log_format_line(ptr, level, format, vl, lineBuffer, lineSize, &printPrefix);

            string line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer)?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(line)) return;

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

        /// <summary>
        /// 实现 IVideoDecoder 接口的 DecodeFrame 方法
        /// </summary>
        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            if (!_isInitialized || _context == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Decoder not initialized, cannot decode frame");
                return -1;
            }

            _decodeTimer.Start();
            FFmpegApi.av_frame_unref(output.Frame);

            if (_needsFlush)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
                _needsFlush = false;
                Logger.Debug?.Print(LogClass.FFmpeg, "Flushed decoder buffers due to previous errors");
            }

            int result;
            if (_useNewApi)
            {
                result = DecodeFrameNewApi(output, bitstream);
            }
            else
            {
                result = DecodeFrameOldApi(output, bitstream);
            }
            
            _decodeTimer.Stop();
            _frameCount++;

            if (_frameCount % 100 == 0)
            {
                double totalTime = _decodeTimer.Elapsed.TotalMilliseconds;
                double avgTime = totalTime / _frameCount;
                double fps = 1000.0 / avgTime;
                
                Logger.Info?.Print(LogClass.FFmpeg, 
                    $"{_decoderType} decode stats: {_frameCount} frames, {avgTime:F2}ms/frame, {fps:F1} FPS, Total: {totalTime:F0}ms");
            }

            return result;
        }

        private int DecodeFrameNewApi(Surface output, ReadOnlySpan<byte> bitstream)
        {
            int result;
            int gotFrame = 0;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                
                result = FFmpegApi.avcodec_send_packet(_context, _packet);
                if (result < 0 && result != FFmpegApi.EAGAIN && result != FFmpegApi.EOF)
                {
                    LogDecodeError("avcodec_send_packet", result);
                    _needsFlush = true;
                    FFmpegApi.av_packet_unref(_packet);
                    return -1;
                }

                result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                if (result >= 0)
                {
                    gotFrame = 1;
                    _isFirstFrame = false;
                    Logger.Debug?.Print(LogClass.FFmpeg, $"Decoded frame: {output.GetFrameInfo()}");
                }
                else if (result == FFmpegApi.EAGAIN || result == FFmpegApi.EOF)
                {
                    gotFrame = 0;
                    result = 0;
                    _isFirstFrame = false;
                }
                else
                {
                    LogDecodeError("avcodec_receive_frame", result);
                    _needsFlush = true;
                    gotFrame = 0;
                }
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }

            return result < 0 ? result : 0;
        }

        private int DecodeFrameOldApi(Surface output, ReadOnlySpan<byte> bitstream)
        {
            if (_context == null || output.Frame == null || _decodeFrame == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Invalid state in DecodeFrameOldApi");
                return -1;
            }

            int result;
            int gotFrame;

            AVPacket* tempPacket = FFmpegApi.av_packet_alloc();
            if (tempPacket == null)
            {
                Logger.Error?.Print(LogClass.FFmpeg, "Failed to allocate temporary packet");
                return -1;
            }

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    tempPacket->Data = ptr;
                    tempPacket->Size = bitstream.Length;
                    result = _decodeFrame(_context, output.Frame, &gotFrame, tempPacket);
                }

                if (result < 0)
                {
                    LogDecodeError("DecodeFrame", result);
                    
                    if (_isFirstFrame || result == -1094995529)
                    {
                        _needsFlush = true;
                        _isFirstFrame = false;
                        FFmpegApi.av_frame_unref(output.Frame);
                        return -1;
                    }
                }

                if (gotFrame == 0)
                {
                    FFmpegApi.av_frame_unref(output.Frame);
                    tempPacket->Data = null;
                    tempPacket->Size = 0;
                    result = _decodeFrame(_context, output.Frame, &gotFrame, tempPacket);
                    _context->HasBFrames = 0;
                }

                if (gotFrame == 0)
                {
                    FFmpegApi.av_frame_unref(output.Frame);
                    return -1;
                }

                _isFirstFrame = false;
                return result < 0 ? result : 0;
            }
            finally
            {
                FFmpegApi.av_packet_unref(tempPacket);
                FFmpegApi.av_packet_free(&tempPacket);
            }
        }

        private void LogDecodeError(string operation, int errorCode)
        {
            string errorType = _useHardwareDecoder ? "hardware" : "software";
            Logger.Warning?.Print(LogClass.FFmpeg, $"{operation} failed with {errorType} decoder (Error: {errorCode})");
        }

        /// <summary>
        /// 实现 IVideoDecoder 接口的 Flush 方法
        /// </summary>
        public void Flush()
        {
            if (_useNewApi && _context != null)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
                Logger.Debug?.Print(LogClass.FFmpeg, "FFmpeg decoder flushed");
            }
        }

        public void Dispose()
        {
            // 释放 Opaque 数据
            if (_context != null && _context->Opaque != IntPtr.Zero)
            {
                var handle = GCHandle.FromIntPtr(_context->Opaque);
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
                _context->Opaque = IntPtr.Zero;
            }

            if (_packet != null)
            {
                fixed (AVPacket** ppPacket = &_packet)
                {
                    FFmpegApi.av_packet_free(ppPacket);
                }
            }

            CleanupHardwareResources();

            if (_useNewApi && _context != null)
            {
                FFmpegApi.avcodec_flush_buffers(_context);
            }

            if (_context != null)
            {
                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }
            
            Logger.Debug?.Print(LogClass.FFmpeg, $"{_decoderType} decoder disposed");
        }

        /// <summary>
        /// 检查硬件解码器支持
        /// </summary>
        public static bool CheckHardwareDecoderSupport(AVCodecID codecId)
        {
            try
            {
                AVHWDeviceType deviceType = FFmpegApi.av_hwdevice_find_type_by_name("mediacodec");
                if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    Logger.Debug?.Print(LogClass.FFmpeg, "MediaCodec hardware device type not supported");
                    return false;
                }

                if (AndroidHardwareDecoders.TryGetValue(codecId, out string[] decoderNames))
                {
                    foreach (string decoderName in decoderNames)
                    {
                        AVCodec* codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                        if (codec != null)
                        {
                            for (int i = 0; ; i++)
                            {
                                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(codec, i);
                                if (config == null) break;
                                
                                if ((config->methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                                    config->device_type == deviceType)
                                {
                                    Logger.Debug?.Print(LogClass.FFmpeg, $"Hardware decoder available: {decoderName} with pix_fmt {config->pix_fmt}");
                                    return true;
                                }
                            }
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.FFmpeg, $"Hardware decoder check failed: {ex.Message}");
                return false;
            }
        }
    }
}
