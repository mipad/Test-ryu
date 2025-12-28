using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private unsafe delegate int AVCodec_decode(AVCodecContext* avctx, void* outdata, int* got_frame_ptr, AVPacket* avpkt);
        private unsafe delegate int GetFormatDelegate(AVCodecContext* ctx, FFmpegApi.AVPixelFormat* pix_fmts);

        private readonly AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private readonly bool _useHardwareDecoding;
        private AVFrame* _hwFrame;
        private AVFrame* _swFrame;
        
        // 调试日志
        private static readonly bool _debugLogging = true;
        private static void DebugLog(string message)
        {
            if (_debugLogging)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"[DEBUG] {message}");
                Console.WriteLine($"[FFmpegContext] {message}");
            }
        }

        public FFmpegContext(AVCodecID codecId)
        {
            DebugLog($"FFmpegContext constructor called with codec: {codecId}");
            
            // 检查是否应该使用硬件解码
            _useHardwareDecoding = ShouldUseHardwareDecoding(codecId);
            DebugLog($"Hardware decoding enabled: {_useHardwareDecoding} for codec: {codecId}");
            
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                string errorMsg = $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.";
                DebugLog(errorMsg);
                Logger.Error?.PrintMsg(LogClass.FFmpeg, errorMsg);
                return;
            }
            
            DebugLog($"Found decoder: {Marshal.PtrToStringAnsi((IntPtr)_codec->Name)}");

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                DebugLog("Codec context couldn't be allocated.");
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }
            
            DebugLog($"Codec context allocated at: 0x{(ulong)_context:X}");

            // 如果支持硬件解码，尝试初始化硬件解码器
            bool hardwareInitialized = false;
            if (_useHardwareDecoding)
            {
                hardwareInitialized = TryInitializeHardwareDecoder();
                if (hardwareInitialized)
                {
                    DebugLog($"Using hardware decoder for {codecId}");
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoder for {codecId}");
                }
                else
                {
                    DebugLog($"Hardware decoder initialization failed, falling back to software for {codecId}");
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using software decoder for {codecId}");
                }
            }
            else
            {
                DebugLog($"Hardware decoding disabled for {codecId}");
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using software decoder for {codecId}");
            }

            // 设置解码器参数
            _context->ThreadCount = Environment.ProcessorCount;
            DebugLog($"Set thread count to: {_context->ThreadCount}");
            
            // 添加性能优化选项
            _context->Flags |= 0x00080000; // AV_CODEC_FLAG_LOW_DELAY
            _context->Flags2 |= 0x00000001; // AV_CODEC_FLAG2_FAST
            
            // 尝试使用AVDictionary设置选项
            AVDictionary* options = null;
            FFmpegApi.av_dict_set(&options, "flags2", "+fast", 0);
            FFmpegApi.av_dict_set(&options, "flags", "+low_delay", 0);
            
            int openResult = FFmpegApi.avcodec_open2(_context, _codec, &options);
            FFmpegApi.av_dict_free(&options);
            
            if (openResult != 0)
            {
                DebugLog($"avcodec_open2 failed with error code: {openResult}");
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }
            
            DebugLog("avcodec_open2 succeeded");

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                DebugLog("Packet couldn't be allocated.");
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }
            
            DebugLog($"Packet allocated at: 0x{(ulong)_packet:X}");

            // 如果需要硬件解码，创建硬件帧
            if (_useHardwareDecoding && hardwareInitialized && _context->HwDeviceCtx != null)
            {
                _hwFrame = FFmpegApi.av_frame_alloc();
                _swFrame = FFmpegApi.av_frame_alloc();
                
                if (_hwFrame != null && _swFrame != null)
                {
                    DebugLog("Hardware frames allocated successfully");
                }
                else
                {
                    DebugLog("Failed to allocate hardware frames");
                }
            }
            else
            {
                DebugLog("Not using hardware frames");
            }

            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;
            
            DebugLog($"FFmpeg version: {avCodecMajorVersion}.{avCodecMinorVersion}");

            // libavcodec 59.24 changed AvCodec to move its private API and also move the codec function to an union.
            if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
                DebugLog("Using FFCodec<AVCodec> decode callback");
            }
            // libavcodec 59.x changed AvCodec private API layout.
            else if (avCodecMajorVersion == 59)
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
                DebugLog("Using FFCodecLegacy<AVCodec501> decode callback");
            }
            // libavcodec 58.x and lower
            else
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
                DebugLog("Using FFCodecLegacy<AVCodec> decode callback");
            }
            
            DebugLog("FFmpegContext initialized successfully");
        }

        private bool ShouldUseHardwareDecoding(AVCodecID codecId)
        {
            DebugLog($"ShouldUseHardwareDecoding called for codec: {codecId}");
            
            // 仅在Android平台上为H264和VP8启用硬件解码
            if (!OperatingSystem.IsAndroid())
            {
                DebugLog("Not on Android platform, hardware decoding disabled");
                return false;
            }
                
            // 只支持H264和VP8
            if (codecId != AVCodecID.AV_CODEC_ID_H264 && codecId != AVCodecID.AV_CODEC_ID_VP8)
            {
                DebugLog($"Codec {codecId} not supported for hardware decoding");
                return false;
            }
                
            DebugLog($"Hardware decoding should be used for {codecId}");
            return true;
        }

        private bool TryInitializeHardwareDecoder()
        {
            DebugLog("TryInitializeHardwareDecoder called");
            
            // 尝试查找MediaCodec硬件解码器
            FFmpegApi.AVHWDeviceType deviceType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
            DebugLog($"Looking for hardware device type: {deviceType} (MediaCodec)");
            
            // 首先检查设备类型是否被支持
            FFmpegApi.AVHWDeviceType* deviceTypePtr = &deviceType;
            FFmpegApi.AVHWDeviceType iter = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            bool deviceTypeSupported = false;
            
            DebugLog("Enumerating supported hardware device types:");
            while (true)
            {
                iter = FFmpegApi.av_hwdevice_iterate_types(iter);
                if (iter == FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                    break;
                    
                string typeName = FFmpegApi.av_hwdevice_get_type_name(iter);
                DebugLog($"  Found device type: {iter} ({typeName})");
                
                if (iter == deviceType)
                {
                    deviceTypeSupported = true;
                }
            }
            
            if (!deviceTypeSupported)
            {
                DebugLog($"Device type {deviceType} is not supported on this system");
                return false;
            }
            
            DebugLog($"Device type {deviceType} is supported");

            // 检查解码器是否支持硬件解码
            AVCodecHWConfig* hwConfig = null;
            int configIndex = 0;
            bool foundHardwareConfig = false;
            
            DebugLog("Checking for hardware configurations:");
            for (int i = 0; ; i++)
            {
                hwConfig = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (hwConfig == null)
                {
                    DebugLog($"No more hardware configs at index {i}");
                    break;
                }
                    
                DebugLog($"  HW Config {i}: DeviceType={hwConfig->DeviceType}, Methods={hwConfig->Methods}");
                
                if ((FFmpegApi.AVHWDeviceType)hwConfig->DeviceType == deviceType && 
                    (hwConfig->Methods & 0x01) != 0) // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
                {
                    foundHardwareConfig = true;
                    configIndex = i;
                    DebugLog($"Found compatible hardware config at index {i}");
                    break;
                }
            }
            
            if (!foundHardwareConfig)
            {
                DebugLog("No MediaCodec hardware configuration found for this codec");
                return false;
            }

            // 创建硬件设备上下文
            DebugLog("Creating hardware device context...");
            AVBufferRef* hwDeviceCtx = FFmpegApi.av_hwdevice_ctx_alloc(deviceType);
            if (hwDeviceCtx == null)
            {
                DebugLog("Failed to allocate hardware device context");
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware device context");
                return false;
            }
            
            DebugLog($"Hardware device context allocated at: 0x{(ulong)hwDeviceCtx:X}");

            // 初始化硬件设备上下文
            DebugLog("Initializing hardware device context...");
            int initResult = FFmpegApi.av_hwdevice_ctx_init(hwDeviceCtx);
            if (initResult < 0)
            {
                DebugLog($"Failed to initialize hardware device context with error: {initResult}");
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to initialize hardware device context");
                FFmpegApi.av_buffer_unref(&hwDeviceCtx);
                return false;
            }
            
            DebugLog("Hardware device context initialized successfully");

            // 设置硬件设备上下文
            _context->HwDeviceCtx = hwDeviceCtx;
            DebugLog($"Set HwDeviceCtx to: 0x{(ulong)hwDeviceCtx:X}");
            
            // 设置像素格式
            _context->PixFmt = (int)hwConfig->PixFmt;
            DebugLog($"Set pixel format to: {_context->PixFmt} (from hwConfig)");
            
            // 设置get_format回调
            DebugLog("Setting GetFormat callback...");
            GetFormatDelegate getFormatDelegate = GetHardwareFormat;
            IntPtr getFormatPtr = Marshal.GetFunctionPointerForDelegate(getFormatDelegate);
            _context->GetFormat = getFormatPtr;
            DebugLog($"GetFormat callback set to: 0x{(ulong)getFormatPtr:X}");
            
            // 保持委托的引用，防止被垃圾回收
            GC.KeepAlive(getFormatDelegate);

            DebugLog($"Initialized MediaCodec hardware decoder successfully");
            return true;
        }
        
        private int GetHardwareFormat(AVCodecContext* ctx, FFmpegApi.AVPixelFormat* pix_fmts)
        {
            DebugLog("GetHardwareFormat callback called");
            
            // 查找支持的像素格式
            int index = 0;
            for (FFmpegApi.AVPixelFormat* p = pix_fmts; *p != FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                DebugLog($"  Checking pixel format {index}: {*p}");
                if (*p == FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
                {
                    DebugLog($"Found MediaCodec pixel format at index {index}");
                    return (int)*p;
                }
                index++;
            }
            
            // 如果没有找到硬件格式，回退到软件解码
            DebugLog("Hardware pixel format not found, falling back to software");
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Hardware pixel format not found, falling back to software");
            FFmpegApi.av_buffer_unref(&ctx->HwDeviceCtx);
            return (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        static FFmpegContext()
        {
            DebugLog("FFmpegContext static constructor called");
            
            _logFunc = Log;

            // Redirect log output.
            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(_logFunc);
            
            DebugLog("FFmpeg logging initialized");
            
            // 打印FFmpeg版本信息
            int version = FFmpegApi.avcodec_version();
            int major = (version >> 16) & 0xFF;
            int minor = (version >> 8) & 0xFF;
            int micro = version & 0xFF;
            
            string ffmpegVersion = $"{major}.{minor}.{micro}";
            DebugLog($"FFmpeg version: {ffmpegVersion}");
            
            // 列出所有支持的硬件设备类型
            DebugLog("Enumerating all supported hardware device types:");
            FFmpegApi.AVHWDeviceType iter = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            while (true)
            {
                iter = FFmpegApi.av_hwdevice_iterate_types(iter);
                if (iter == FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                    break;
                    
                string typeName = FFmpegApi.av_hwdevice_get_type_name(iter);
                DebugLog($"  {iter}: {typeName}");
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

            string line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer).Trim();
            
            // 添加FFmpeg原生日志到调试日志
            DebugLog($"[FFmpeg Native] {line}");

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
            DebugLog($"DecodeFrame called: bitstream length={bitstream.Length}, surface={output?.GetType().Name}");
            
            if (_useHardwareDecoding && _context->HwDeviceCtx != null && _hwFrame != null && _swFrame != null)
            {
                DebugLog("Using hardware decoding path");
                return DecodeFrameHardware(output, bitstream);
            }
            else
            {
                DebugLog("Using software decoding path");
                return DecodeFrameSoftware(output, bitstream);
            }
        }
        
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            DebugLog("DecodeFrameHardware called");
            
            if (_hwFrame == null || _swFrame == null)
            {
                DebugLog("Hardware frames not allocated, falling back to software");
                return DecodeFrameSoftware(output, bitstream);
            }
            
            FFmpegApi.av_frame_unref(_hwFrame);
            FFmpegApi.av_frame_unref(_swFrame);
            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame = 0;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                DebugLog($"Calling decode function with packet: size={bitstream.Length}");
                result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
                DebugLog($"Decode result: {result}, gotFrame: {gotFrame}");
            }

            if (gotFrame == 0)
            {
                DebugLog("Frame not ready, trying to get delayed frame");
                
                // 如果帧未送达，可能是延迟的
                // 通过传递0长度包获取下一个延迟帧
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
                DebugLog($"Delayed decode result: {result}, gotFrame: {gotFrame}");
                
                // 将B帧设置为0，因为我们已经消耗了所有延迟帧
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                DebugLog("No frame available after decode");
                return -1;
            }

            DebugLog($"Hardware frame decoded: width={_hwFrame->Width}, height={_hwFrame->Height}, format={_hwFrame->Format}");

            // 从硬件帧传输到软件帧
            DebugLog("Transferring data from hardware to software frame...");
            int transferResult = FFmpegApi.av_hwframe_transfer_data(_swFrame, _hwFrame, 0);
            if (transferResult < 0)
            {
                DebugLog($"Failed to transfer frame from hardware to software: {transferResult}");
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to transfer frame from hardware to software");
                FFmpegApi.av_frame_unref(_hwFrame);
                FFmpegApi.av_frame_unref(_swFrame);
                return -1;
            }
            
            DebugLog($"Frame transfer successful: sw_frame width={_swFrame->Width}, height={_swFrame->Height}, format={_swFrame->Format}");
            
            // 复制数据到输出帧
            DebugLog("Copying frame data to output...");
            CopyFrameData(_swFrame, output.Frame);
            
            FFmpegApi.av_frame_unref(_hwFrame);
            FFmpegApi.av_frame_unref(_swFrame);
            
            DebugLog($"Hardware decode successful, returning: {result}");
            return result < 0 ? result : 0;
        }
        
        private int DecodeFrameSoftware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            DebugLog("DecodeFrameSoftware called");
            
            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame = 0;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                DebugLog($"Calling decode function with packet: size={bitstream.Length}");
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                DebugLog($"Decode result: {result}, gotFrame: {gotFrame}");
            }

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                DebugLog("Frame not ready, trying to get delayed frame");

                // 如果帧未送达，可能是延迟的
                // 通过传递0长度包获取下一个延迟帧
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
                DebugLog($"Delayed decode result: {result}, gotFrame: {gotFrame}");

                // 将B帧设置为0，因为我们已经消耗了所有延迟帧
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                DebugLog("No frame available after decode");
                return -1;
            }

            DebugLog($"Software frame decoded: width={output.Frame->Width}, height={output.Frame->Height}, format={output.Frame->Format}");
            return result < 0 ? result : 0;
        }
        
        private unsafe void CopyFrameData(AVFrame* src, AVFrame* dst)
        {
            DebugLog($"CopyFrameData: src={src->Width}x{src->Height}, format={src->Format}");
            
            // 复制基本属性
            dst->Width = src->Width;
            dst->Height = src->Height;
            dst->Format = src->Format;
            dst->InterlacedFrame = src->InterlacedFrame;
            
            DebugLog($"Copied basic attributes: width={dst->Width}, height={dst->Height}, format={dst->Format}");
            
            // 复制行大小
            for (int i = 0; i < 4; i++)
            {
                dst->LineSize[i] = src->LineSize[i];
                if (src->LineSize[i] > 0)
                {
                    DebugLog($"  LineSize[{i}] = {dst->LineSize[i]}");
                }
            }
            
            // 复制数据指针
            for (int i = 0; i < 4; i++)
            {
                dst->Data[i] = src->Data[i];
                if (src->Data[i] != null)
                {
                    DebugLog($"  Data[{i}] pointer copied");
                }
            }
            
            // 对于YUV420P格式，我们需要确保正确的平面排列
            if (src->Format == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P)
            {
                DebugLog("Frame is YUV420P format");
                // YUV420P格式已经正确处理
            }
            
            DebugLog("Frame data copy completed");
        }

        public void Dispose()
        {
            DebugLog("FFmpegContext.Dispose() called");
            
            if (_hwFrame != null)
            {
                DebugLog($"Freeing hardware frame: 0x{(ulong)_hwFrame:X}");
                FFmpegApi.av_frame_unref(_hwFrame);
                FFmpegApi.av_free(_hwFrame);
                _hwFrame = null;
            }
            
            if (_swFrame != null)
            {
                DebugLog($"Freeing software frame: 0x{(ulong)_swFrame:X}");
                FFmpegApi.av_frame_unref(_swFrame);
                FFmpegApi.av_free(_swFrame);
                _swFrame = null;
            }
            
            if (_packet != null)
            {
                DebugLog($"Freeing packet: 0x{(ulong)_packet:X}");
                fixed (AVPacket** ppPacket = &_packet)
                {
                    FFmpegApi.av_packet_free(ppPacket);
                }
            }

            if (_context != null)
            {
                DebugLog($"Closing codec context: 0x{(ulong)_context:X}");
                FFmpegApi.avcodec_close(_context);
                
                DebugLog($"Freeing codec context: 0x{(ulong)_context:X}");
                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }
            
            DebugLog("FFmpegContext disposed successfully");
        }
    }
}
