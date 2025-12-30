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
        private IntPtr _hwDeviceCtx;
        private bool _useHardwareDecoding;
        private bool _isMediaCodecDecoder;
        private bool _forceSoftwareDecode;
        private object _decodeLock = new object();
        private AVFrame* _hwFrame;
        
        // 参考hw_decode.c，需要硬件像素格式和硬件设备类型
        private static FFmpegApi.AVPixelFormat _hwPixelFormat = FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE;
        private static FFmpegApi.AVHWDeviceType _hwDeviceType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

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
            
            // 参考hw_decode.c，先获取支持的硬件配置
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");
            
            // 检查硬件解码支持
            if (_useHardwareDecoding)
            {
                // 参考hw_decode.c中的循环查找硬件配置
                for (int i = 0;; i++)
                {
                    var hwConfigPtr = FFmpegApi.avcodec_get_hw_config(_codec, i);
                    if (hwConfigPtr == IntPtr.Zero)
                    {
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, $"No hardware config found for decoder {codecId}");
                        break;
                    }
                    
                    var hwConfig = (AVCodecHWConfig*)hwConfigPtr;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"Hardware config[{i}]: PixFmt={hwConfig->PixFmt}, " +
                        $"Methods={hwConfig->Methods}, DeviceType={hwConfig->DeviceType}");
                    
                    // 参考hw_decode.c中的检查条件
                    if ((hwConfig->Methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                        hwConfig->DeviceType == (int)FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)
                    {
                        _hwPixelFormat = (FFmpegApi.AVPixelFormat)hwConfig->PixFmt;
                        _hwDeviceType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
                        _isMediaCodecDecoder = true;
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                            $"Found matching hardware config for MediaCodec: PixelFormat={_hwPixelFormat}");
                        break;
                    }
                }
                
                // 如果找到硬件配置，尝试寻找硬件解码器
                if (_isMediaCodecDecoder)
                {
                    var hardwareDecoder = FindHardwareDecoder(codecId);
                    if (hardwareDecoder != null)
                    {
                        _codec = hardwareDecoder;
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                            $"Using hardware decoder: {Marshal.PtrToStringUTF8((IntPtr)_codec->Name)}");
                    }
                    else
                    {
                        Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                            "Hardware decoder not found, falling back to software");
                        _useHardwareDecoding = false;
                        _isMediaCodecDecoder = false;
                    }
                }
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated codec context: 0x{(ulong)_context:X}");

            // 参考hw_decode.c中的初始化流程
            if (_useHardwareDecoding && _isMediaCodecDecoder)
            {
                // 设置get_format回调 - 参考hw_decode.c中的get_hw_format函数
                // 注意：这里需要C#委托，但由于AVCodecContext是原生结构，我们无法直接设置C#回调
                // 因此我们直接设置像素格式
                _context->PixFmt = (int)_hwPixelFormat;
                
                // 初始化硬件设备上下文 - 参考hw_decode.c中的hw_decoder_init函数
                if (InitHardwareDeviceContext() < 0)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                        "Hardware device context initialization failed, falling back to software");
                    _useHardwareDecoding = false;
                    _isMediaCodecDecoder = false;
                    // 重置像素格式为软件格式
                    _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                }
            }

            // 设置解码器参数
            if (_context->PrivData != IntPtr.Zero)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Setting zero latency tune");
                FFmpegApi.av_opt_set((void*)_context->PrivData, "tune", "zerolatency", 0);
            }
            
            _context->ThreadCount = 0;
            _context->ThreadType &= ~2;
            
            // 硬件解码优化设置
            if (_isMediaCodecDecoder && _useHardwareDecoding)
            {
                _context->ThreadCount = 1;
                _context->ThreadType = 0;
                _context->Flags |= 0x0001; // CODEC_FLAG_LOW_DELAY
                _context->Flags2 |= 0x00000100; // AV_CODEC_FLAG2_FAST
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    "Set MediaCodec decoder options: single thread, low delay mode, fast decoding");
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"Codec context before open: HwDeviceCtx=0x{(ulong)_context->HwDeviceCtx:X}, " +
                $"PixFmt={_context->PixFmt}");
            
            // 打开编解码器 - 参考hw_decode.c中的流程
            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_open2 result: {openResult}");
            
            if (openResult != 0)
            {
                // 获取错误信息
                byte* errorBuffer = stackalloc byte[256];
                if (FFmpegApi.av_strerror(openResult, errorBuffer, 256) == 0)
                {
                    string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                        $"Codec couldn't be opened. Error: {errorMsg} (code: {openResult})");
                }
                
                // 硬件解码失败，回退到软件解码
                if (_useHardwareDecoding)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, 
                        "Hardware decoder failed to open, trying software decoder...");
                    
                    // 清理硬件设备上下文
                    if (_hwDeviceCtx != IntPtr.Zero)
                    {
                        FFmpegApi.av_buffer_unref(&_hwDeviceCtx);
                        _hwDeviceCtx = IntPtr.Zero;
                    }
                    
                    // 重新分配编解码器上下文
                    FFmpegApi.avcodec_free_context(&_context);
                    _codec = FFmpegApi.avcodec_find_decoder(codecId);
                    _context = FFmpegApi.avcodec_alloc_context3(_codec);
                    
                    if (_context == null)
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                            "Failed to allocate software codec context");
                        return;
                    }
                    
                    _useHardwareDecoding = false;
                    _isMediaCodecDecoder = false;
                    
                    // 重新打开软件编解码器
                    openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"Software avcodec_open2 result: {openResult}");
                }
                
                if (openResult != 0)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                        $"Decoder failed to open. Error code: {openResult}");
                    return;
                }
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"Codec opened successfully. Pixel format: {_context->PixFmt}, " +
                $"Hardware device context: 0x{(ulong)_context->HwDeviceCtx:X}");

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated packet: 0x{(ulong)_packet:X}");

            // 分配硬件帧（仅当硬件解码启用时）
            if (_useHardwareDecoding && _isMediaCodecDecoder)
            {
                _hwFrame = FFmpegApi.av_frame_alloc();
                if (_hwFrame == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware frame");
                    _useHardwareDecoding = false;
                }
                else
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"Allocated hardware frame: 0x{(ulong)_hwFrame:X}");
                }
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                "Using new FFmpeg API (avcodec_send_packet/avcodec_receive_frame)");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"FFmpegContext created successfully. IsMediaCodec: {_isMediaCodecDecoder}, " +
                $"HardwareDecoding: {_useHardwareDecoding}");
        }

        // 参考hw_decode.c中的hw_decoder_init函数
        private int InitHardwareDeviceContext()
        {
            try
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    "Initializing hardware device context for MediaCodec");
                
                // 创建硬件设备上下文 - 参考hw_decode.c中的av_hwdevice_ctx_create调用
                int result = FFmpegApi.av_hwdevice_ctx_create(
                    &_hwDeviceCtx, 
                    _hwDeviceType, 
                    null, 
                    null, 
                    0);
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"av_hwdevice_ctx_create result: {result}, deviceCtx: 0x{(ulong)_hwDeviceCtx:X}");
                
                if (result < 0)
                {
                    byte* errorBuffer = stackalloc byte[256];
                    if (FFmpegApi.av_strerror(result, errorBuffer, 256) == 0)
                    {
                        string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                            $"Failed to create hardware device context: {errorMsg} (code: {result})");
                    }
                    return result;
                }
                
                // 设置硬件设备上下文到编解码器上下文 - 参考hw_decode.c中的ctx->hw_device_ctx = av_buffer_ref(hw_device_ctx)
                _context->HwDeviceCtx = (AVBufferRef*)_hwDeviceCtx;
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Set hardware device context: 0x{(ulong)_context->HwDeviceCtx:X}");
                
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Exception initializing hardware device context: {ex.Message}\n{ex.StackTrace}");
                return -1;
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
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                            $"Found Android hardware decoder: {decoderName}");
                        return codec;
                    }
                }
            }

            return null;
        }

        private bool ShouldUseHardwareDecoding(AVCodecID codecId)
        {
            bool isAndroid = IsAndroidRuntime();
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"Checking hardware decoding for codec {codecId} on platform: Android={isAndroid}");
            
            if (!isAndroid)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    "Hardware decoding disabled: Not on Android platform");
                return false;
            }
                
            if (codecId != AVCodecID.AV_CODEC_ID_H264 && codecId != AVCodecID.AV_CODEC_ID_VP8)
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Hardware decoding disabled: Codec {codecId} not supported for hardware decoding");
                return false;
            }
                
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"Hardware decoding enabled for codec {codecId} on Android platform");
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
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                    $"DecodeFrame called. Bitstream size: {bitstream.Length}, " +
                    $"IsMediaCodec: {_isMediaCodecDecoder}, UseHardware: {_useHardwareDecoding}");
                
                if (_isMediaCodecDecoder && _useHardwareDecoding && !_forceSoftwareDecode)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using MediaCodec hardware decoder");
                    return DecodeFrameHardware(output, bitstream);
                }
                
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using software decoding path");
                return DecodeFrameSoftware(output, bitstream);
            }
        }
        
        // 参考hw_decode.c中的decode_write函数
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameHardware called");
            
            if (_hwFrame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware frame is null");
                return -1;
            }
            
            FFmpegApi.av_frame_unref(_hwFrame);

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                        $"Decoding packet with size: {bitstream.Length}");
                    
                    // 发送数据包 - 参考hw_decode.c中的avcodec_send_packet
                    int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                    
                    if (sendResult < 0)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(sendResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                $"avcodec_send_packet failed: {errorMsg} (code: {sendResult})");
                        }
                    }
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    FFmpegApi.av_packet_unref(_packet);
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        return sendResult;
                    }
                    
                    // 接收帧 - 参考hw_decode.c中的avcodec_receive_frame
                    int receiveResult = FFmpegApi.avcodec_receive_frame(_context, _hwFrame);
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                    
                    if (receiveResult < 0)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(receiveResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                $"avcodec_receive_frame failed: {errorMsg} (code: {receiveResult})");
                        }
                    }
                    
                    if (receiveResult == 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                            $"Hardware decode successful. Frame: Width={_hwFrame->Width}, " +
                            $"Height={_hwFrame->Height}, Format={_hwFrame->Format}");
                        
                        // 检查是否为硬件格式 - 参考hw_decode.c中的frame->format == hw_pix_fmt
                        if (_hwFrame->Format == (int)_hwPixelFormat)
                        {
                            // 从硬件帧转换到软件帧 - 参考hw_decode.c中的av_hwframe_transfer_data
                            if (output.TransferFromHardwareFrame(_hwFrame))
                            {
                                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                    $"Frame converted to software format. Output: " +
                                    $"Width={output.Frame->Width}, Height={output.Frame->Height}");
                                return 0;
                            }
                            else
                            {
                                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                    "Failed to transfer frame from hardware");
                                return -1;
                            }
                        }
                        else
                        {
                            // 如果不是硬件格式，直接使用该帧
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                "Frame is already in software format");
                            // 这里需要将_hwFrame的数据复制到output.Frame
                            // 简化处理：使用转换函数
                            return output.TransferFromHardwareFrame(_hwFrame) ? 0 : -1;
                        }
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
                        return receiveResult;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Exception in DecodeFrameHardware: {ex.Message}\n{ex.StackTrace}");
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
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                        $"Decoding packet with size: {bitstream.Length}");
                    
                    int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                        $"avcodec_send_packet result: {sendResult}");
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(sendResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                $"avcodec_send_packet failed: {errorMsg} (code: {sendResult})");
                        }
                    }
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    FFmpegApi.av_packet_unref(_packet);
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        return sendResult;
                    }
                    
                    int receiveResult = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                        $"avcodec_receive_frame result: {receiveResult}");
                    
                    if (receiveResult < 0 && receiveResult != FFmpegApi.AVERROR.EAGAIN && receiveResult != FFmpegApi.AVERROR.EOF)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(receiveResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                $"avcodec_receive_frame failed: {errorMsg} (code: {receiveResult})");
                        }
                    }
                    
                    if (receiveResult == 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                            $"Software decode completed successfully. Frame: " +
                            $"Width={output.Frame->Width}, Height={output.Frame->Height}");
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
                        return receiveResult;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Exception in DecodeFrameSoftware: {ex.Message}\n{ex.StackTrace}");
                return -1;
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

            if (_hwDeviceCtx != IntPtr.Zero)
            {
                FFmpegApi.av_buffer_unref(&_hwDeviceCtx);
                _hwDeviceCtx = IntPtr.Zero;
            }

            if (_packet != null)
            {
                FFmpegApi.av_packet_free(&_packet);
            }

            if (_context != null)
            {
                FFmpegApi.avcodec_close(_context);
                FFmpegApi.avcodec_free_context(&_context);
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpegContext disposed");
        }
    }
}
