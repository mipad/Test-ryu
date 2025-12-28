using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;

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

        public FFmpegContext(AVCodecID codecId)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            _useHardwareDecoding = ShouldUseHardwareDecoding(codecId);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled: {_useHardwareDecoding}");
            
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }
            
            string codecName = Marshal.PtrToStringUTF8((IntPtr)_codec->Name) ?? "Unknown";
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found codec: {codecName}");

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            if (_useHardwareDecoding)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Attempting to initialize hardware decoder...");
                _hardwareDecoderInitialized = TryInitializeHardwareDecoder();
                if (_hardwareDecoderInitialized)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoder for {codecName} with device type: {_hwDeviceType}");
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder initialization failed, falling back to software for {codecName}");
                }
            }
            else
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using software decoder for {codecName}");
            }

            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec opened successfully. Pixel format: {_context->PixFmt}, Hardware device context: {(_context->HwDeviceCtx != null ? "Set" : "Not set")}");

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            if (_hardwareDecoderInitialized && _context->HwDeviceCtx != null)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Creating hardware and software frames...");
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

        private bool ShouldUseHardwareDecoding(AVCodecID codecId)
        {
            bool isAndroid = IsAndroidRuntime();
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Checking hardware decoding for codec {codecId} on platform: Android={isAndroid}, RID={RuntimeInformation.RuntimeIdentifier}");
            
            bool platformSupported = isAndroid || OperatingSystem.IsLinux();
            
            if (!platformSupported)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoding disabled: Platform not supported for hardware decoding");
                return false;
            }
                
            if (codecId != AVCodecID.AV_CODEC_ID_H264 && codecId != AVCodecID.AV_CODEC_ID_VP8)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding disabled: Codec {codecId} not supported for hardware decoding (only H264 and VP8)");
                return false;
            }
                
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled for codec {codecId} on current platform");
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
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Preferred hardware device types for platform: {string.Join(", ", preferredDeviceTypes)}");
            
            AVCodecHWConfig* hwConfig = null;
            int configIndex = 0;
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Checking available hardware configurations...");
            
            // 先检查所有可用的硬件配置
            var availableConfigs = new List<string>();
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
                
                string configInfo = $"Config {i}: DeviceType={deviceTypeName}, PixFmt={hwConfig->PixFmt}, Methods={hwConfig->Methods}";
                availableConfigs.Add(configInfo);
                Logger.Info?.PrintMsg(LogClass.FFmpeg, configInfo);
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Available hardware configurations: {string.Join("; ", availableConfigs)}");
            
            // 现在尝试初始化
            for (int i = 0; i <= configIndex; i++)
            {
                hwConfig = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (hwConfig == null)
                    continue;
                    
                var deviceType = (FFmpegApi.AVHWDeviceType)hwConfig->DeviceType;
                
                // 如果这个设备类型在我们的优先列表中，并且支持硬件设备上下文
                if (preferredDeviceTypes.Contains(deviceType) && 
                    (hwConfig->Methods & 0x01) != 0) // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found matching hardware configuration at index {i}: {deviceType}");
                    
                    // 尝试创建硬件设备上下文
                    if (TryCreateHardwareContext(deviceType, hwConfig->PixFmt))
                    {
                        return true;
                    }
                }
            }
            
            // 如果没有找到优先的设备类型，尝试任何可用的硬件解码器
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "No preferred hardware decoder found, trying any available hardware decoder...");
            for (int i = 0; i <= configIndex; i++)
            {
                hwConfig = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (hwConfig == null)
                    continue;
                    
                var deviceType = (FFmpegApi.AVHWDeviceType)hwConfig->DeviceType;
                
                if ((hwConfig->Methods & 0x01) != 0) // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Trying alternative hardware configuration at index {i}: {deviceType}");
                    
                    if (TryCreateHardwareContext(deviceType, hwConfig->PixFmt))
                    {
                        return true;
                    }
                }
            }
            
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"No suitable hardware configuration found. Checked {configIndex + 1} configurations.");
            return false;
        }
        
        private bool TryCreateHardwareContext(FFmpegApi.AVHWDeviceType deviceType, FFmpegApi.AVPixelFormat pixelFormat)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Attempting to create hardware context for {deviceType} with pixel format {pixelFormat}");
            
            AVBufferRef* hwDeviceCtx = FFmpegApi.av_hwdevice_ctx_alloc(deviceType);
            if (hwDeviceCtx == null)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Failed to allocate hardware device context for {deviceType}");
                return false;
            }
            
            int initResult = FFmpegApi.av_hwdevice_ctx_init(hwDeviceCtx);
            if (initResult < 0)
            {
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Failed to initialize hardware device context for {deviceType}, error code: {initResult}");
                FFmpegApi.av_buffer_unref(&hwDeviceCtx);
                return false;
            }
            
            _context->HwDeviceCtx = hwDeviceCtx;
            _hwPixelFormat = (int)pixelFormat;
            _hwDeviceType = deviceType;
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware pixel format: {_hwPixelFormat}");
            
            GetFormatDelegate getFormatDelegate = GetHardwareFormat;
            IntPtr getFormatPtr = Marshal.GetFunctionPointerForDelegate(getFormatDelegate);
            _context->GetFormat = getFormatPtr;
            
            GC.KeepAlive(getFormatDelegate);
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Initialized hardware decoder successfully: DeviceType={deviceType}, PixelFormat={_hwPixelFormat}");
            return true;
        }
        
        private List<FFmpegApi.AVHWDeviceType> GetPreferredDeviceTypes()
        {
            var preferredTypes = new List<FFmpegApi.AVHWDeviceType>();
            
            if (IsAndroidRuntime())
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Android platform detected");
                
                // Android API级别检查
                int androidApiLevel = GetAndroidApiLevel();
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Android API Level: {androidApiLevel}");
                
                // MediaCodec需要API级别16+，但更好的支持需要21+
                if (androidApiLevel >= 21)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "API level >= 21, preferring MediaCodec hardware decoder");
                    preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC);
                }
                else
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"API level {androidApiLevel} is too low for reliable MediaCodec support");
                }
                
                // 添加Vulkan作为备用
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);
                
                // 如果是Android 9+，还可以尝试Vulkan
                if (androidApiLevel >= 28) // Android 9 (Pie)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "API level >= 28, also considering Vulkan hardware decoder");
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Linux platform detected, preferring CUDA/VAAPI/VDPAU hardware decoders");
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA);
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI);
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU);
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);
            }
            else if (OperatingSystem.IsWindows())
            {
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA);
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2);
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA);
            }
            else if (OperatingSystem.IsMacOS())
            {
                preferredTypes.Add(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX);
            }
            
            return preferredTypes;
        }
        
        private int GetAndroidApiLevel()
        {
            try
            {
                // 尝试从环境变量获取API级别
                string apiLevelStr = Environment.GetEnvironmentVariable("ANDROID_API_LEVEL");
                if (!string.IsNullOrEmpty(apiLevelStr) && int.TryParse(apiLevelStr, out int apiLevel))
                {
                    return apiLevel;
                }
                
                // 尝试从构建属性获取
                string buildPropPath = "/system/build.prop";
                if (System.IO.File.Exists(buildPropPath))
                {
                    var lines = System.IO.File.ReadAllLines(buildPropPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("ro.build.version.sdk="))
                        {
                            if (int.TryParse(line.Substring("ro.build.version.sdk=".Length), out apiLevel))
                            {
                                return apiLevel;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Failed to get Android API level: {ex.Message}");
            }
            
            // 默认返回一个合理的API级别
            return 30; // Android 11
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
            if (_hardwareDecoderInitialized && _context->HwDeviceCtx != null)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoding path with device type: {_hwDeviceType}");
                return DecodeFrameHardware(output, bitstream);
            }
            else
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using software decoding path");
                return DecodeFrameSoftware(output, bitstream);
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
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No frame decoded");
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
