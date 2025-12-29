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
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);
        private unsafe delegate int GetFormatDelegate(AVCodecContext* ctx, int* pix_fmts);

        private readonly AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private bool _useHardwareDecoding;
        private AVFrame* _hwFrame;
        private AVFrame* _swFrame;
        private int _hwPixelFormat;
        private FFmpegApi.AVHWDeviceType _hwDeviceType;
        private bool _hardwareDecoderInitialized;
        private bool _isMediaCodecDecoder;
        private bool _useNewApi;
        private IntPtr _swsContext;
        private bool _forceSoftwareDecode;
        private object _decodeLock = new object();
        private bool _isH264Codec;

        // 根据yuzu代码，首选的GPU解码器
        private static readonly FFmpegApi.AVHWDeviceType[] PreferredGpuDecoders = 
        {
#if WINDOWS
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA,
#elif ANDROID
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC,
#elif LINUX
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU,
#endif
            FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN,
        };

        // 首选GPU格式
        private const int PreferredGpuFormat = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_NV12;
        // 首选CPU格式
        private const int PreferredCpuFormat = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;

        // Android硬件解码器名称映射
        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
        };

        public FFmpegContext(AVCodecID codecId)
        {
            _isH264Codec = codecId == AVCodecID.AV_CODEC_ID_H264;
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            // 检查是否强制使用软件解码
            _forceSoftwareDecode = Environment.GetEnvironmentVariable("RYUJINX_FORCE_SOFTWARE_DECODE") == "1";
            
            _useHardwareDecoding = !_forceSoftwareDecode && ShouldUseHardwareDecoding(codecId);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled: {_useHardwareDecoding}");
            
            // 查找编解码器
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 设置编解码器选项（参考yuzu）
            // 设置低延迟模式
            FFmpegApi.av_opt_set(_context->PrivData, "tune", "zerolatency", 0);
            
            // 设置线程数为0（自动选择）
            _context->ThreadCount = 0;
            
            // 禁用帧级多线程
            _context->ThreadType &= ~2; // ~FF_THREAD_FRAME
            
            // 尝试初始化硬件解码
            if (_useHardwareDecoding)
            {
                _hardwareDecoderInitialized = TryInitializeHardwareDecoder();
                if (_hardwareDecoderInitialized)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder initialized successfully: {_hwDeviceType}");
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoder initialization failed, falling back to software");
                    _context->PixFmt = PreferredCpuFormat;
                }
            }
            else
            {
                _context->PixFmt = PreferredCpuFormat;
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            
            // 打开编解码器上下文
            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec opened successfully. Pixel format: {_context->PixFmt}, HW Device Context: {_context->HwDeviceCtx != null}");

            // 分配数据包
            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            // 如果需要硬件解码，创建硬件帧和软件帧用于传输
            if (_hardwareDecoderInitialized && _context->HwDeviceCtx != null)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Creating hardware and software frames for hardware decoder...");
                _hwFrame = FFmpegApi.av_frame_alloc();
                _swFrame = FFmpegApi.av_frame_alloc();
                
                if (_hwFrame == null || _swFrame == null)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware/software frames");
                    _hardwareDecoderInitialized = false;
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware and software frames allocated successfully");
                }
            }

            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}");

            // 检查是否支持新的API（FFmpeg 4.0+）
            _useNewApi = avCodecMajorVersion >= 58; // FFmpeg 4.0 (58.x) 引入了新API
            
            if (_useNewApi)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using new FFmpeg API (avcodec_send_packet/avcodec_receive_frame)");
            }
            else
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using legacy FFmpeg API");
                
                // 根据版本获取解码函数指针
                // 注意：这里的类型转换需要根据实际结构体定义调整
                if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using FFmpeg >= 59.24 API");
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                }
                else if (avCodecMajorVersion == 59)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using FFmpeg 59.x API");
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                }
                else
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using FFmpeg <= 58.x API");
                    _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                }
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext created successfully. Hardware decoder: {_hardwareDecoderInitialized}, UseNewApi: {_useNewApi}");
        }

        private bool ShouldUseHardwareDecoding(AVCodecID codecId)
        {
            bool isAndroid = IsAndroidRuntime();
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Checking hardware decoding for codec {codecId} on platform: Android={isAndroid}, RID={RuntimeInformation.RuntimeIdentifier}");
            
            // 只在Android平台启用硬件解码
            if (!isAndroid)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoding disabled: Not on Android platform");
                return false;
            }
            
            // 只支持H264的硬件解码
            if (codecId != AVCodecID.AV_CODEC_ID_H264)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding disabled: Codec {codecId} not supported for hardware decoding (only H264)");
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

        private bool TryInitializeHardwareDecoder()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Attempting to initialize hardware decoder...");
            
            // 获取支持的设备类型
            var supportedTypes = GetSupportedDeviceTypes();
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Supported device types: {string.Join(", ", supportedTypes)}");
            
            // 尝试首选GPU解码器
            foreach (var type in PreferredGpuDecoders)
            {
                FFmpegApi.AVPixelFormat hwPixelFormat;
                
                if (!supportedTypes.Contains(type))
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Device type {type} not supported");
                    continue;
                }
                
                // 检查编解码器是否支持此设备类型
                if (!SupportsDecodingOnDevice(out hwPixelFormat, type))
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Codec does not support device type {type}");
                    continue;
                }
                
                // 尝试创建设备上下文
                if (!InitializeHardwareDeviceContext(type))
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Failed to initialize device context for {type}");
                    continue;
                }
                
                // 设置硬件像素格式
                _hwPixelFormat = (int)hwPixelFormat;
                _hwDeviceType = type;
                
                // 设置get_format回调
                GetFormatDelegate getFormatDelegate = GetGpuFormat;
                IntPtr getFormatPtr = Marshal.GetFunctionPointerForDelegate(getFormatDelegate);
                _context->GetFormat = getFormatPtr;
                
                GC.KeepAlive(getFormatDelegate);
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder initialized: DeviceType={type}, PixelFormat={_hwPixelFormat}");
                return true;
            }
            
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No suitable hardware decoder found");
            return false;
        }
        
        private List<FFmpegApi.AVHWDeviceType> GetSupportedDeviceTypes()
        {
            var types = new List<FFmpegApi.AVHWDeviceType>();
            FFmpegApi.AVHWDeviceType currentType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            
            while (true)
            {
                currentType = FFmpegApi.av_hwdevice_iterate_types(currentType);
                if (currentType == FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    break;
                }
                
                types.Add(currentType);
            }
            
            return types;
        }
        
        private bool SupportsDecodingOnDevice(out FFmpegApi.AVPixelFormat hwPixelFormat, FFmpegApi.AVHWDeviceType type)
        {
            hwPixelFormat = FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE;
            
            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (config == null)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Codec does not support device type {type}");
                    break;
                }
                
                if ((config->Methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 && 
                    (FFmpegApi.AVHWDeviceType)config->DeviceType == type)
                {
                    hwPixelFormat = (FFmpegApi.AVPixelFormat)config->PixFmt;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec supports device type {type} with pixel format {hwPixelFormat}");
                    return true;
                }
            }
            
            return false;
        }
        
        private bool InitializeHardwareDeviceContext(FFmpegApi.AVHWDeviceType type)
        {
            AVBufferRef* deviceCtx = null;
            
            // 创建设备上下文
            int result = FFmpegApi.av_hwdevice_ctx_create(&deviceCtx, type, null, null, 0);
            if (result < 0)
            {
                string typeName = "Unknown";
                try
                {
                    typeName = FFmpegApi.av_hwdevice_get_type_name(type);
                }
                catch { }
                
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"av_hwdevice_ctx_create({typeName}) failed: {result}");
                return false;
            }
            
            // 设置设备上下文
            _context->HwDeviceCtx = deviceCtx;
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware device context created for {FFmpegApi.av_hwdevice_get_type_name(type)}");
            return true;
        }
        
        private int GetGpuFormat(AVCodecContext* ctx, int* pix_fmts)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"GetGpuFormat callback called. Looking for pixel format: {_hwPixelFormat}");
            
            if (pix_fmts == null)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "pix_fmts is null, falling back to software");
                FFmpegApi.av_buffer_unref(&ctx->HwDeviceCtx);
                _hardwareDecoderInitialized = false;
                return PreferredCpuFormat;
            }
            
            // 检查请求的像素格式是否在支持的列表中
            for (int* p = pix_fmts; *p != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == _hwPixelFormat)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found requested pixel format: {*p}");
                    return *p;
                }
            }
            
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Hardware pixel format {_hwPixelFormat} not found in supported list, falling back to software");
            FFmpegApi.av_buffer_unref(&ctx->HwDeviceCtx);
            _hardwareDecoderInitialized = false;
            return PreferredCpuFormat;
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
            lock (_decodeLock) // 防止多线程并发调用
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"DecodeFrame called. Bitstream size: {bitstream.Length}, Hardware decoder initialized: {_hardwareDecoderInitialized}, UseNewApi: {_useNewApi}");
                
                if (_hardwareDecoderInitialized && !_forceSoftwareDecode && _context->HwDeviceCtx != null)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoder");
                    return DecodeFrameHardware(output, bitstream);
                }
                else
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using software decoding path");
                    return DecodeFrameSoftware(output, bitstream);
                }
            }
        }
        
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameHardware called");
            
            if (_hwFrame == null || _swFrame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware or software frames not allocated");
                return DecodeFrameSoftware(output, bitstream);
            }
            
            // 清除帧引用
            FFmpegApi.av_frame_unref(_hwFrame);
            FFmpegApi.av_frame_unref(_swFrame);
            FFmpegApi.av_frame_unref(output.Frame);

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decoding packet with size: {bitstream.Length}");
                    
                    if (_useNewApi)
                    {
                        // 使用新的API
                        int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                        
                        // 清除数据包
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);
                        
                        if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {sendResult}");
                            return sendResult;
                        }
                        
                        // 接收帧
                        int receiveResult = FFmpegApi.avcodec_receive_frame(_context, _hwFrame);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                        
                        if (receiveResult == 0)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame decoded successfully, transferring from hardware to software...");
                            
                            // 从硬件帧传输到软件帧
                            if (FFmpegApi.av_hwframe_transfer_data(_swFrame, _hwFrame, 0) < 0)
                            {
                                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to transfer frame from hardware to software");
                                FFmpegApi.av_frame_unref(_hwFrame);
                                FFmpegApi.av_frame_unref(_swFrame);
                                return -1;
                            }
                            
                            // 复制数据到输出帧
                            CopyFrameData(_swFrame, output.Frame);
                            
                            FFmpegApi.av_frame_unref(_hwFrame);
                            FFmpegApi.av_frame_unref(_swFrame);
                            
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Hardware decode completed successfully");
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
                    else
                    {
                        // 使用旧的API
                        int result;
                        int gotFrame = 0;
                        
                        result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Legacy decode result: {result}, Got frame: {gotFrame}");
                        
                        // 清除数据包
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);
                        
                        if (gotFrame == 0)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame not delivered, trying delayed frame...");
                            result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
                            
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Delayed frame decode result: {result}, Got frame: {gotFrame}");
                            
                            _context->HasBFrames = 0;
                        }
                        
                        if (gotFrame == 0)
                        {
                            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No frame decoded");
                            return -1;
                        }
                        
                        if (result < 0)
                        {
                            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Decode error: {result}");
                            return result;
                        }
                        
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame decoded successfully, transferring from hardware to software...");
                        
                        // 从硬件帧传输到软件帧
                        if (FFmpegApi.av_hwframe_transfer_data(_swFrame, _hwFrame, 0) < 0)
                        {
                            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to transfer frame from hardware to software");
                            FFmpegApi.av_frame_unref(_hwFrame);
                            FFmpegApi.av_frame_unref(_swFrame);
                            return -1;
                        }
                        
                        // 复制数据到输出帧
                        CopyFrameData(_swFrame, output.Frame);
                        
                        FFmpegApi.av_frame_unref(_hwFrame);
                        FFmpegApi.av_frame_unref(_swFrame);
                        
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Hardware decode completed successfully");
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception in DecodeFrameHardware: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }
        
        private int DecodeFrameSoftware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameSoftware called");
            
            FFmpegApi.av_frame_unref(output.Frame);
            
            // 确保输出帧有缓冲区
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
                    
                    int result = 0;
                    
                    if (_useNewApi)
                    {
                        // 使用新的API
                        int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                        
                        // 清除数据包
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);
                        
                        if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {sendResult}");
                            return sendResult;
                        }
                        
                        // 接收帧
                        result = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {result}");
                        
                        if (result == 0)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Software decode completed successfully");
                            return 0;
                        }
                        else if (result == FFmpegApi.AVERROR.EAGAIN)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "No frame available yet (EAGAIN)");
                            return -1;
                        }
                        else if (result == FFmpegApi.AVERROR.EOF)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "End of stream (EOF)");
                            return 0;
                        }
                        else
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame failed: {result}");
                            return result;
                        }
                    }
                    else
                    {
                        // 使用旧的API
                        int gotFrame = 0;
                        
                        result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Legacy decode result: {result}, Got frame: {gotFrame}");
                        
                        // 清除数据包
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);
                        
                        if (gotFrame == 0)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame not delivered, trying delayed frame...");
                            result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                            
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Delayed frame decode result: {result}, Got frame: {gotFrame}");
                            
                            _context->HasBFrames = 0;
                        }
                        
                        if (gotFrame == 0)
                        {
                            FFmpegApi.av_frame_unref(output.Frame);
                            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No frame decoded");
                            return -1;
                        }
                        
                        if (result < 0)
                        {
                            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Decode error: {result}");
                            return result;
                        }
                        
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Software decode completed with result: {result}");
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception in DecodeFrameSoftware: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }
        
        private unsafe void CopyFrameData(AVFrame* src, AVFrame* dst)
        {
            dst->Width = src->Width;
            dst->Height = src->Height;
            dst->Format = src->Format;
            dst->InterlacedFrame = src->InterlacedFrame;
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Copying frame data: Width={src->Width}, Height={src->Height}, Format={src->Format}");
            
            for (int i = 0; i < 8; i++)
            {
                dst->LineSize[i] = src->LineSize[i];
                dst->Data[i] = src->Data[i];
            }
        }

        public void Dispose()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Disposing FFmpegContext");
            
            if (_swsContext != IntPtr.Zero)
            {
                FFmpegApi.sws_freeContext(_swsContext);
                _swsContext = IntPtr.Zero;
            }
            
            if (_hwFrame != null)
            {
                fixed (AVFrame** ppFrame = &_hwFrame)
                {
                    FFmpegApi.av_frame_free(ppFrame);
                }
                _hwFrame = null;
            }
            
            if (_swFrame != null)
            {
                fixed (AVFrame** ppFrame = &_swFrame)
                {
                    FFmpegApi.av_frame_free(ppFrame);
                }
                _swFrame = null;
            }
            
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
