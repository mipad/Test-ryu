using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

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

        // Android硬件解码器名称映射
        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
        };

        public FFmpegContext(AVCodecID codecId)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            _useHardwareDecoding = ShouldUseHardwareDecoding(codecId);
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
                _isMediaCodecDecoder = true;
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 如果是MediaCodec解码器，不需要额外的硬件设备上下文初始化
            // 否则尝试初始化硬件解码器（如Vulkan）
            if (_useHardwareDecoding && !_isMediaCodecDecoder)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Attempting to initialize hardware decoder context...");
                _hardwareDecoderInitialized = TryInitializeHardwareDecoder();
                if (_hardwareDecoderInitialized)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder context initialized successfully: {_hwDeviceType}");
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoder context initialization failed");
                }
            }
            else if (_isMediaCodecDecoder)
            {
                _hardwareDecoderInitialized = true;
                _hwDeviceType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using MediaCodec hardware decoder (no additional context needed)");
            }

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

            // 如果需要硬件解码，创建硬件帧和软件帧
            if (_hardwareDecoderInitialized && !_isMediaCodecDecoder && _context->HwDeviceCtx != null)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Creating hardware and software frames for Vulkan decoder...");
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
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Android hardware decoder not found: {decoderName}");
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
            if (_hardwareDecoderInitialized)
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
        
        private int DecodeFrameMediaCodec(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameMediaCodec called");
            
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

            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"MediaCodec decode completed with result: {result}");
            return result < 0 ? result : 0;
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
