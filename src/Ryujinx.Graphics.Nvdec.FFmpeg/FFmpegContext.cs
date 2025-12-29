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
        private readonly bool _useHardwareDecoding;
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

        // Android硬件解码器名称映射
        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
        };

        public FFmpegContext(AVCodecID codecId)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            // 检查是否强制使用软件解码
            _forceSoftwareDecode = Environment.GetEnvironmentVariable("RYUJINX_FORCE_SOFTWARE_DECODE") == "1";
            
            _useHardwareDecoding = !_forceSoftwareDecode && ShouldUseHardwareDecoding(codecId);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled: {_useHardwareDecoding}");
            
            // 首先尝试查找硬件解码器
            _codec = FindHardwareDecoder(codecId);
            if (_codec == null)
            {
                // 如果没找到硬件解码器，使用通用解码器
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
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoder: {codecName}, IsMediaCodec: {_isMediaCodecDecoder}");
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 设置解码器选项
            _context->ThreadCount = 0; // 自动选择线程数
            _context->ThreadType = 2; // FF_THREAD_FRAME
            
            if (_isMediaCodecDecoder)
            {
                // MediaCodec解码器优化设置
                _context->ThreadCount = 1; // MediaCodec最好使用单线程
                _context->ThreadType = 0; // 禁用多线程
                _context->Flags |= 0x0001; // CODEC_FLAG_LOW_DELAY
                _context->Flags2 |= 0x00000100; // AV_CODEC_FLAG2_FAST (开启快速解码模式)
                
                // 设置MediaCodec特定的参数
                _context->RefcountedFrames = 1; // 使用引用计数的帧
                _context->SkipFrame = 0; // AVDISCARD_NONE，不跳过任何帧
                _context->SkipIdct = 0; // AVDISCARD_NONE
                _context->SkipLoopFilter = 0; // AVDISCARD_NONE
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Set MediaCodec decoder options: single thread, low delay mode, fast decoding");
            }

            // 尝试初始化硬件设备上下文（主要用于非MediaCodec的硬件解码器）
            if (_useHardwareDecoding && !_isMediaCodecDecoder)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Attempting to initialize Vulkan hardware decoder context...");
                _hardwareDecoderInitialized = TryInitializeHardwareDecoder();
                if (_hardwareDecoderInitialized)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Vulkan hardware decoder context initialized successfully: {_hwDeviceType}");
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Vulkan hardware decoder context initialization failed");
                }
            }
            else if (_isMediaCodecDecoder)
            {
                // MediaCodec不需要额外的硬件设备上下文
                _hardwareDecoderInitialized = true;
                _hwDeviceType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using MediaCodec hardware decoder (no additional context needed)");
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec opened successfully. Pixel format: {_context->PixFmt}, Width: {_context->Width}, Height: {_context->Height}");

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            // 如果需要硬件解码，创建硬件帧和软件帧（仅非MediaCodec需要）
            if (_hardwareDecoderInitialized && !_isMediaCodecDecoder)
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
            else if (_isMediaCodecDecoder)
            {
                // 为MediaCodec创建软件帧用于可能的格式转换
                _swFrame = FFmpegApi.av_frame_alloc();
                if (_swFrame == null)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to allocate software frame for MediaCodec");
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
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext created successfully. Hardware decoder: {_hardwareDecoderInitialized}, IsMediaCodec: {_isMediaCodecDecoder}, UseNewApi: {_useNewApi}");
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

        private bool TryInitializeHardwareDecoder()
        {
            List<FFmpegApi.AVHWDeviceType> preferredDeviceTypes = GetPreferredDeviceTypes();
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Preferred hardware device types: {string.Join(", ", preferredDeviceTypes)}");
            
            AVCodecHWConfig* hwConfig = null;
            int configIndex = 0;
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Checking available hardware configurations...");
            
            for (int i = 0; ; i++)
            {
                hwConfig = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (hwConfig == null)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"No more hardware configurations at index {i}");
                    break;
                }
                
                configIndex = i;
                
                string deviceTypeName = $"Unknown({hwConfig->DeviceType})";
                try
                {
                    deviceTypeName = ((FFmpegApi.AVHWDeviceType)hwConfig->DeviceType).ToString();
                }
                catch { }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware config {i}: DeviceType={deviceTypeName}, PixFmt={hwConfig->PixFmt}, Methods={hwConfig->Methods}");
                
                var deviceType = (FFmpegApi.AVHWDeviceType)hwConfig->DeviceType;
                if (preferredDeviceTypes.Contains(deviceType) && 
                    (hwConfig->Methods & 0x01) != 0)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found matching hardware configuration at index {i}: {deviceType}");
                    
                    AVBufferRef* hwDeviceCtx = FFmpegApi.av_hwdevice_ctx_alloc(deviceType);
                    if (hwDeviceCtx == null)
                    {
                        Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Failed to allocate hardware device context for {deviceType}");
                        continue;
                    }
                    
                    int initResult = FFmpegApi.av_hwdevice_ctx_init(hwDeviceCtx);
                    if (initResult < 0)
                    {
                        Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Failed to initialize hardware device context for {deviceType}, error code: {initResult}");
                        FFmpegApi.av_buffer_unref(&hwDeviceCtx);
                        continue;
                    }
                    
                    _context->HwDeviceCtx = hwDeviceCtx;
                    _hwPixelFormat = (int)hwConfig->PixFmt;
                    _hwDeviceType = deviceType;
                    
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware pixel format: {_hwPixelFormat}");
                    
                    GetFormatDelegate getFormatDelegate = GetHardwareFormat;
                    IntPtr getFormatPtr = Marshal.GetFunctionPointerForDelegate(getFormatDelegate);
                    _context->GetFormat = getFormatPtr;
                    
                    GC.KeepAlive(getFormatDelegate);
                    
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder context initialized successfully: DeviceType={deviceType}, PixelFormat={_hwPixelFormat}");
                    return true;
                }
            }
            
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"No suitable hardware configuration found. Checked {configIndex + 1} configurations.");
            return false;
        }
        
        private List<FFmpegApi.AVHWDeviceType> GetPreferredDeviceTypes()
        {
            var preferredTypes = new List<FFmpegApi.AVHWDeviceType>();
            
            if (IsAndroidRuntime())
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Android platform detected, preferring Vulkan hardware decoder");
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);
            }
            
            return preferredTypes;
        }
        
        private int GetHardwareFormat(AVCodecContext* ctx, int* pix_fmts)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"GetHardwareFormat callback called. Looking for pixel format: {_hwPixelFormat}");
            
            if (pix_fmts == null)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "pix_fmts is null, falling back to software");
                FFmpegApi.av_buffer_unref(&ctx->HwDeviceCtx);
                _hardwareDecoderInitialized = false;
                return (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
            }
            
            int index = 0;
            for (int* p = pix_fmts; *p != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Checking pixel format {index}: {*p}");
                if (*p == _hwPixelFormat)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found requested pixel format: {*p}");
                    return *p;
                }
                index++;
            }
            
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Hardware pixel format {_hwPixelFormat} not found in supported list, falling back to software");
            FFmpegApi.av_buffer_unref(&ctx->HwDeviceCtx);
            _hardwareDecoderInitialized = false;
            return (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
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
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"DecodeFrame called. Bitstream size: {bitstream.Length}, Hardware decoder initialized: {_hardwareDecoderInitialized}, IsMediaCodec: {_isMediaCodecDecoder}, UseNewApi: {_useNewApi}");
                
                if (_hardwareDecoderInitialized && !_forceSoftwareDecode)
                {
                    if (_isMediaCodecDecoder)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using MediaCodec hardware decoder");
                        return DecodeFrameMediaCodec(output, bitstream);
                    }
                    else if (_context->HwDeviceCtx != null)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using Vulkan hardware decoder");
                        return DecodeFrameHardware(output, bitstream);
                    }
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
                    
                    if (_useNewApi)
                    {
                        // 使用新API
                        int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                        
                        // 清空packet
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
                        
                        // 尝试接收帧
                        int receiveResult = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                        
                        if (receiveResult == 0)
                        {
                            // 解码成功，检查格式
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                $"MediaCodec decode successful. Frame: Width={output.Frame->Width}, Height={output.Frame->Height}, " +
                                $"Format={output.Frame->Format}, Linesize0={output.Frame->LineSize[0]}");
                            
                            // 检查并转换格式
                            if (output.Frame->Format != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P)
                            {
                                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Converting frame format {output.Frame->Format} to YUV420P");
                                if (!ConvertFrameFormat(output))
                                {
                                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Frame format conversion failed");
                                    return -1;
                                }
                            }
                            
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
                        // 使用旧API
                        int result;
                        int gotFrame = 0;
                        
                        result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Legacy decode result: {result}, Got frame: {gotFrame}");
                        
                        // 清空packet
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);

                        if (gotFrame == 1)
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                $"MediaCodec decode completed successfully. Frame: Width={output.Frame->Width}, Height={output.Frame->Height}, " +
                                $"Format={output.Frame->Format}");
                            
                            // 检查并转换格式
                            if (output.Frame->Format != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P)
                            {
                                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Converting frame format {output.Frame->Format} to YUV420P");
                                if (!ConvertFrameFormat(output))
                                {
                                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Frame format conversion failed");
                                    return -1;
                                }
                            }
                            
                            return 0;
                        }
                        else
                        {
                            // 尝试延迟帧
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame not delivered, trying delayed frame...");
                            result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                            
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Delayed frame decode result: {result}, Got frame: {gotFrame}");
                            
                            if (gotFrame == 0)
                            {
                                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No frame decoded from MediaCodec");
                                FFmpegApi.av_frame_unref(output.Frame);
                                return -1;
                            }
                            
                            // 检查并转换格式
                            if (output.Frame->Format != (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P)
                            {
                                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Converting frame format {output.Frame->Format} to YUV420P");
                                if (!ConvertFrameFormat(output))
                                {
                                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Frame format conversion failed");
                                    return -1;
                                }
                            }
                            
                            return 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception in DecodeFrameMediaCodec: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }
        
        private unsafe bool ConvertFrameFormat(Surface output)
        {
            if (_swsContext == IntPtr.Zero)
            {
                // 创建格式转换上下文
                _swsContext = FFmpegApi.sws_getContext(
                    output.Width, output.Height, (FFmpegApi.AVPixelFormat)output.PixelFormat,
                    output.Width, output.Height, FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P,
                    2, // SWS_BILINEAR
                    IntPtr.Zero, IntPtr.Zero, null);
                
                if (_swsContext == IntPtr.Zero)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to create SWS context for format conversion");
                    return false;
                }
            }
            
            // 创建临时帧用于存储转换结果
            if (_swFrame == null)
            {
                _swFrame = FFmpegApi.av_frame_alloc();
                if (_swFrame == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate software frame for conversion");
                    return false;
                }
            }
            
            FFmpegApi.av_frame_unref(_swFrame);
            _swFrame->Format = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
            _swFrame->Width = output.Width;
            _swFrame->Height = output.Height;
            
            if (FFmpegApi.av_frame_get_buffer(_swFrame, 32) < 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate software frame buffer");
                return false;
            }
            
            // 执行格式转换
            int result = FFmpegApi.sws_scale(
                _swsContext,
                output.Frame->Data, output.Frame->LineSize,
                0, output.Height,
                _swFrame->Data, _swFrame->LineSize);
            
            if (result <= 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Failed to convert frame format: {result}");
                return false;
            }
            
            // 复制转换后的数据回输出帧
            CopyFrameData(_swFrame, output.Frame);
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Frame format converted successfully: {output.PixelFormat} -> YUV420P");
            return true;
        }
        
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameHardware called");
            
            if (_hwFrame == null || _swFrame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware or software frames not allocated");
                return DecodeFrameSoftware(output, bitstream);
            }
            
            FFmpegApi.av_frame_unref(_hwFrame);
            FFmpegApi.av_frame_unref(_swFrame);
            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decoding packet with size: {bitstream.Length}");
                result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
            }

            if (gotFrame == 0)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame not delivered, trying delayed frame...");
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
                
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                return -1;
            }

            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame decoded successfully, transferring from hardware to software...");
            
            if (FFmpegApi.av_hwframe_transfer_data(_swFrame, _hwFrame, 0) < 0)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to transfer frame from hardware to software");
                FFmpegApi.av_frame_unref(_hwFrame);
                FFmpegApi.av_frame_unref(_swFrame);
                return -1;
            }
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame transferred successfully, copying to output...");
            
            CopyFrameData(_swFrame, output.Frame);
            
            FFmpegApi.av_frame_unref(_hwFrame);
            FFmpegApi.av_frame_unref(_swFrame);
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Hardware decode completed with result: {result}");
            return result < 0 ? result : 0;
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

            int result;
            int gotFrame;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decoding packet with size: {bitstream.Length}");
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
            }

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);

                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Frame not delivered, trying delayed frame...");
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);

                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No frame decoded");
                return -1;
            }

            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Software decode completed with result: {result}");
            return result < 0 ? result : 0;
        }
        
        private unsafe void CopyFrameData(AVFrame* src, AVFrame* dst)
        {
            dst->Width = src->Width;
            dst->Height = src->Height;
            dst->Format = src->Format;
            dst->InterlacedFrame = src->InterlacedFrame;
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Copying frame data: Width={src->Width}, Height={src->Height}, Format={src->Format}");
            
            for (int i = 0; i < 4; i++)
            {
                dst->LineSize[i] = src->LineSize[i];
            }
            
            for (int i = 0; i < 4; i++)
            {
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
                FFmpegApi.av_frame_unref(_hwFrame);
                FFmpegApi.av_free(_hwFrame);
                _hwFrame = null;
            }
            
            if (_swFrame != null)
            {
                FFmpegApi.av_frame_unref(_swFrame);
                FFmpegApi.av_free(_swFrame);
                _swFrame = null;
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
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpegContext disposed");
        }
    }
}
