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
        private AVBufferRef* _hwFrameCtx;
        private bool _useHardwareDecoding;
        private bool _isMediaCodecDecoder;
        private object _decodeLock = new object();
        private AVFrame* _hwFrame;
        
        // 参考hw_decode.c，需要硬件像素格式
        private static FFmpegApi.AVPixelFormat _hwPixelFormat = FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE;

        private static readonly Dictionary<AVCodecID, string[]> AndroidHardwareDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, new[] { "h264_mediacodec" } },
            { AVCodecID.AV_CODEC_ID_VP8, new[] { "vp8_mediacodec" } },
        };

        public FFmpegContext(AVCodecID codecId)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for codec: {codecId}");
            
            // 强制使用硬件解码，不检查环境变量
            _useHardwareDecoding = true;
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware decoding enabled: {_useHardwareDecoding}");
            
            // 首先尝试查找硬件解码器
            _codec = FindHardwareDecoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder not found for codec: {codecId}");
                return;
            }
            
            _isMediaCodecDecoder = true;
            string codecName = Marshal.PtrToStringUTF8((IntPtr)_codec->Name) ?? "unknown";
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found hardware decoder: {codecName}");

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated codec context: 0x{(ulong)_context:X}");

            // 配置硬件解码
            if (!ConfigureHardwareDecoding(codecId))
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware decoding configuration failed");
                return;
            }

            // 设置解码器参数
            if (_context->PrivData != IntPtr.Zero)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Setting zero latency tune");
                FFmpegApi.av_opt_set((void*)_context->PrivData, "tune", "zerolatency", 0);
            }
            
            _context->ThreadCount = 1;
            _context->ThreadType = 0;
            _context->Flags |= 0x0001; // CODEC_FLAG_LOW_DELAY
            _context->Flags2 |= 0x00000100; // AV_CODEC_FLAG2_FAST
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Set hardware decoder options: single thread, low delay mode, fast decoding");

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"Codec context before open: HwDeviceCtx=0x{(ulong)_context->HwDeviceCtx:X}, " +
                $"PixFmt={_context->PixFmt}, HwFramesCtx=0x{(ulong)_context->HwFramesCtx:X}");
            
            // 打开编解码器
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
                
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware decoder failed to open");
                return;
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

            // 分配硬件帧
            _hwFrame = FFmpegApi.av_frame_alloc();
            if (_hwFrame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware frame");
                return;
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated hardware frame: 0x{(ulong)_hwFrame:X}");

            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                "Using new FFmpeg API (avcodec_send_packet/avcodec_receive_frame)");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                $"FFmpegContext created successfully. IsMediaCodec: {_isMediaCodecDecoder}, " +
                $"HardwareDecoding: {_useHardwareDecoding}");
        }

        private bool ConfigureHardwareDecoding(AVCodecID codecId)
        {
            try
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Configuring hardware decoding for MediaCodec");
                
                // 参考hw_decode.c，先查找硬件配置
                for (int i = 0;; i++)
                {
                    IntPtr hwConfigPtr = FFmpegApi.avcodec_get_hw_config(_codec, i);
                    if (hwConfigPtr == IntPtr.Zero)
                    {
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, $"No more hardware configs at index {i}");
                        break;
                    }
                    
                    var hwConfig = (AVCodecHWConfig*)hwConfigPtr;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"Hardware config[{i}]: PixFmt={hwConfig->PixFmt}, " +
                        $"Methods={hwConfig->Methods}, DeviceType={hwConfig->DeviceType}");
                    
                    if ((hwConfig->Methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                        hwConfig->DeviceType == (int)FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)
                    {
                        _hwPixelFormat = (FFmpegApi.AVPixelFormat)hwConfig->PixFmt;
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                            $"Found matching hardware config: PixelFormat={_hwPixelFormat}");
                        break;
                    }
                }
                
                if (_hwPixelFormat == FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "No suitable hardware pixel format found");
                    return false;
                }
                
                // 创建硬件设备上下文 - 参考hw_decode.c中的hw_decoder_init
                int result;
                fixed (IntPtr* hwDeviceCtxPtr = &_hwDeviceCtx)
                {
                    result = FFmpegApi.av_hwdevice_ctx_create(
                        hwDeviceCtxPtr, 
                        FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC, 
                        null, 
                        null, 
                        0);
                }
                
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
                    return false;
                }
                
                // 设置硬件设备上下文到编解码器上下文
                _context->HwDeviceCtx = (AVBufferRef*)_hwDeviceCtx;
                
                if (_context->HwDeviceCtx == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to set hardware device context");
                    return false;
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Set hardware device context: 0x{(ulong)_context->HwDeviceCtx:X}");
                
                // 创建硬件帧上下文
                IntPtr hwFrameCtx = FFmpegApi.av_hwdevice_ctx_alloc(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC);
                if (hwFrameCtx == IntPtr.Zero)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware frame context");
                    return false;
                }
                
                _hwFrameCtx = (AVBufferRef*)hwFrameCtx;
                
                // 初始化硬件帧上下文
                result = FFmpegApi.av_hwframe_ctx_init(_hwFrameCtx);
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"av_hwframe_ctx_init result: {result}");
                
                if (result < 0)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Failed to initialize hardware frame context: {result}");
                    return false;
                }
                
                // 设置硬件帧上下文到编解码器上下文
                _context->HwFramesCtx = _hwFrameCtx;
                
                // 设置像素格式
                _context->PixFmt = (int)_hwPixelFormat;
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Set pixel format to hardware format: {_hwPixelFormat}");
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoding configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Exception configuring hardware decoding: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private unsafe AVCodec* FindHardwareDecoder(AVCodecID codecId)
        {
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

            Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"No hardware decoder found for codec: {codecId}");
            return null;
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
                    $"IsMediaCodec: {_isMediaCodecDecoder}, HardwareDecoding: {_useHardwareDecoding}");
                
                if (_isMediaCodecDecoder && _useHardwareDecoding)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using MediaCodec hardware decoder");
                    return DecodeFrameHardware(output, bitstream);
                }
                
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware decoding not available");
                return -1;
            }
        }
        
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameHardware called");
            
            if (_hwFrame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware frame is null");
                return -1;
            }
            
            FFmpegApi.av_frame_unref(_hwFrame);
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Unref hardware frame");

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                        $"Decoding packet with size: {bitstream.Length}");
                    
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
                            $"Height={_hwFrame->Height}, Format={_hwFrame->Format}, " +
                            $"Linesize0={_hwFrame->LineSize[0]}");
                        
                        // 检查是否为硬件格式
                        if (_hwFrame->Format == (int)_hwPixelFormat)
                        {
                            // 从硬件帧转换到软件帧
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                "Starting hardware frame transfer to software frame");
                            if (output.TransferFromHardwareFrame(_hwFrame))
                            {
                                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                    $"Frame converted to software format. Output: " +
                                    $"Width={output.Frame->Width}, Height={output.Frame->Height}, " +
                                    $"Format={output.Frame->Format}, Linesize0={output.Frame->LineSize[0]}");
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

        public void Dispose()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Disposing FFmpegContext");
            
            if (_hwFrame != null)
            {
                FFmpegApi.av_frame_unref(_hwFrame);
                FFmpegApi.av_free(_hwFrame);
                _hwFrame = null;
            }

            if (_hwFrameCtx != null)
            {
                fixed (AVBufferRef** ppHwFrameCtx = &_hwFrameCtx)
                {
                    IntPtr* ppRef = (IntPtr*)ppHwFrameCtx;
                    FFmpegApi.av_buffer_unref(ppRef);
                }
            }

            if (_hwDeviceCtx != IntPtr.Zero)
            {
                fixed (IntPtr* ppRef = &_hwDeviceCtx)
                {
                    FFmpegApi.av_buffer_unref(ppRef);
                }
                _hwDeviceCtx = IntPtr.Zero;
            }

            if (_packet != null)
            {
                fixed (AVPacket** ppPacket = &_packet)
                {
                    FFmpegApi.av_packet_free(ppPacket);
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
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpegContext disposed");
        }
    }
}
