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
        private AVBufferRef* _hwFrameCtx; // 修改类型
        private bool _useHardwareDecoding;
        private bool _isMediaCodecDecoder;
        private bool _forceSoftwareDecode;
        private object _decodeLock = new object();
        private AVFrame* _hwFrame;
        private AVMediaCodecContext* _mediaCodecCtx;

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
            
            _codec = FindHardwareDecoder(codecId);
            if (_codec == null)
            {
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
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found hardware decoder: {codecName}, IsMediaCodec: {_isMediaCodecDecoder}");
                
                if (_isMediaCodecDecoder && !_forceSoftwareDecode)
                {
                    _useHardwareDecoding = true;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "MediaCodec decoder detected, forcing hardware decoding");
                }
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated codec context: 0x{(ulong)_context:X}");

            // 配置硬件解码
            if (_useHardwareDecoding && _isMediaCodecDecoder)
            {
                if (!ConfigureMediaCodecDecoding())
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "MediaCodec hardware decoding configuration failed, falling back to software");
                    _useHardwareDecoding = false;
                    _isMediaCodecDecoder = false;
                }
            }

            if (_context->PrivData != IntPtr.Zero)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Setting zero latency tune, PrivData: 0x{(ulong)_context->PrivData:X}");
                FFmpegApi.av_opt_set((void*)_context->PrivData, "tune", "zerolatency", 0);
            }
            else
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "PrivData is null, skipping zero latency tune");
            }
            
            _context->ThreadCount = 0;
            _context->ThreadType &= ~2;
            
            if (_isMediaCodecDecoder && _useHardwareDecoding)
            {
                _context->ThreadCount = 1;
                _context->ThreadType = 0;
                _context->Flags |= 0x0001;
                _context->Flags2 |= 0x00000100;
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Set MediaCodec decoder options: single thread, low delay mode, fast decoding");
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec context before open: HwDeviceCtx=0x{(ulong)_context->HwDeviceCtx:X}, PixFmt={_context->PixFmt}");
            
            int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_open2 result: {openResult}");
            
            if (openResult != 0)
            {
                // 获取详细的错误信息
                byte* errorBuffer = stackalloc byte[256];
                if (FFmpegApi.av_strerror(openResult, errorBuffer, 256) == 0)
                {
                    string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened. Error: {errorMsg} (code: {openResult})");
                }
                else
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec couldn't be opened. Error code: {openResult}");
                }
                
                // 尝试软件解码作为回退
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Hardware decoder failed to open, trying software decoder...");
                _useHardwareDecoding = false;
                _isMediaCodecDecoder = false;
                
                // 清理硬件设备上下文
                if (_context->HwDeviceCtx != null)
                {
                    _context->HwDeviceCtx = null;
                }
                
                // 重置像素格式
                _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
                
                // 重新打开编解码器
                openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Software avcodec_open2 result: {openResult}");
                
                if (openResult != 0)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Software decoder also failed to open. Error code: {openResult}");
                    return;
                }
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec opened successfully. Pixel format: {_context->PixFmt}, Hardware device context: 0x{(ulong)_context->HwDeviceCtx:X}");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Codec context after open: ThreadCount={_context->ThreadCount}, ThreadType={_context->ThreadType}");

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
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated hardware frame: 0x{(ulong)_hwFrame:X}");
                }
            }

            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Using new FFmpeg API (avcodec_send_packet/avcodec_receive_frame)");
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext created successfully. IsMediaCodec: {_isMediaCodecDecoder}, HardwareDecoding: {_useHardwareDecoding}");
        }

        private bool ConfigureMediaCodecDecoding()
        {
            try
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Configuring MediaCodec hardware decoding with surface");
                
                // 首先创建硬件设备上下文
                IntPtr deviceCtx = IntPtr.Zero;
                int result = FFmpegApi.av_hwdevice_ctx_create(&deviceCtx, 
                    FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC, 
                    null, null, 0);
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"av_hwdevice_ctx_create result: {result}, deviceCtx: 0x{(ulong)deviceCtx:X}");
                
                if (result < 0)
                {
                    byte* errorBuffer = stackalloc byte[256];
                    if (FFmpegApi.av_strerror(result, errorBuffer, 256) == 0)
                    {
                        string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Failed to create hardware device context: {errorMsg} (code: {result})");
                    }
                    return false;
                }
                
                _hwDeviceCtx = deviceCtx;
                
                // 创建MediaCodec上下文
                _mediaCodecCtx = FFmpegApi.av_mediacodec_alloc_context();
                if (_mediaCodecCtx == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate MediaCodec context");
                    return false;
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated MediaCodec context: 0x{(ulong)_mediaCodecCtx:X}");
                
                // 初始化MediaCodec上下文（使用null surface，因为我们不需要渲染到屏幕）
                // 注意：有些MediaCodec实现可能需要一个有效的surface，即使只是用于解码
                result = FFmpegApi.av_mediacodec_default_init(_context, _mediaCodecCtx, IntPtr.Zero);
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"av_mediacodec_default_init result: {result}");
                
                if (result < 0)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "MediaCodec initialization with null surface failed, trying without surface");
                    // 继续尝试，有些设备可能允许null surface
                }
                
                // 设置硬件设备上下文到编解码器上下文
                _context->HwDeviceCtx = (AVBufferRef*)_hwDeviceCtx;
                
                if (_context->HwDeviceCtx == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to set hardware device context");
                    return false;
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Set hardware device context: 0x{(ulong)_context->HwDeviceCtx:X}");
                
                // 创建硬件帧上下文
                var hwFrameCtx = FFmpegApi.av_hwdevice_ctx_alloc(FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC);
                if (hwFrameCtx == IntPtr.Zero)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware frame context");
                    return false;
                }
                
                _hwFrameCtx = (AVBufferRef*)hwFrameCtx; // 转换为正确的类型
                
                // 初始化硬件帧上下文
                result = FFmpegApi.av_hwframe_ctx_init(_hwFrameCtx); // 修复：传递AVBufferRef*类型
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"av_hwframe_ctx_init result: {result}");
                
                if (result < 0)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Failed to initialize hardware frame context: {result}");
                    return false;
                }
                
                // 设置硬件帧上下文到编解码器上下文
                _context->HwFramesCtx = _hwFrameCtx; // 直接使用AVBufferRef*类型
                
                // 查找硬件配置
                for (int i = 0; ; i++)
                {
                    var hwConfigPtr = FFmpegApi.avcodec_get_hw_config(_codec, i);
                    if (hwConfigPtr == IntPtr.Zero)
                    {
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, $"No more hardware configs at index {i}");
                        break;
                    }
                    
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found hardware config at index {i}: 0x{(ulong)hwConfigPtr:X}");
                    
                    unsafe
                    {
                        var hwConfig = (AVCodecHWConfig*)hwConfigPtr;
                        Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Hardware config: PixFmt={hwConfig->PixFmt}, Methods={hwConfig->Methods}, DeviceType={hwConfig->DeviceType}");
                        
                        if ((hwConfig->Methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                            hwConfig->DeviceType == (int)FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)
                        {
                            _context->PixFmt = hwConfig->PixFmt;
                            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found matching hardware config: PixelFormat={hwConfig->PixFmt}");
                            break;
                        }
                    }
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "MediaCodec hardware decoding configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception configuring MediaCodec decoding: {ex.Message}\n{ex.StackTrace}");
                return false;
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
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"DecodeFrame called. Bitstream size: {bitstream.Length}, IsMediaCodec: {_isMediaCodecDecoder}, UseHardware: {_useHardwareDecoding}");
                
                if (_isMediaCodecDecoder && _useHardwareDecoding && !_forceSoftwareDecode)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Using MediaCodec hardware decoder");
                    return DecodeFrameHardware(output, bitstream);
                }
                
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Using software decoding path");
                return DecodeFrameSoftware(output, bitstream);
            }
        }
        
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "DecodeFrameHardware called");
            
            if (output.Frame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Output frame is null");
                return -1;
            }
            
            if (_hwFrame == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Hardware frame is null");
                return -1;
            }
            
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Hardware frame address: 0x{(ulong)_hwFrame:X}");
            
            FFmpegApi.av_frame_unref(_hwFrame);
            Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Unref hardware frame");

            try
            {
                fixed (byte* ptr = bitstream)
                {
                    _packet->Data = ptr;
                    _packet->Size = bitstream.Length;
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Decoding packet with size: {bitstream.Length}, data ptr: 0x{(ulong)ptr:X}");
                    
                    int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                    
                    if (sendResult < 0)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(sendResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {errorMsg} (code: {sendResult})");
                        }
                    }
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    FFmpegApi.av_packet_unref(_packet);
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {sendResult}");
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
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame failed: {errorMsg} (code: {receiveResult})");
                        }
                    }
                    
                    if (receiveResult == 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                            $"Hardware decode successful. Frame: Width={_hwFrame->Width}, Height={_hwFrame->Height}, " +
                            $"Format={_hwFrame->Format}, Linesize0={_hwFrame->LineSize[0]}");
                        
                        // 从硬件帧转换到软件帧
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, "Starting hardware frame transfer to software frame");
                        if (output.TransferFromHardwareFrame(_hwFrame))
                        {
                            Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                                $"Frame converted to software format. Output: Width={output.Frame->Width}, Height={output.Frame->Height}, " +
                                $"Format={output.Frame->Format}, Linesize0={output.Frame->LineSize[0]}");
                            return 0;
                        }
                        else
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to transfer frame from hardware");
                            return -1;
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
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame failed: {receiveResult}");
                        return receiveResult;
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
                    
                    int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(sendResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {errorMsg} (code: {sendResult})");
                        }
                    }
                    
                    _packet->Data = null;
                    _packet->Size = 0;
                    FFmpegApi.av_packet_unref(_packet);
                    
                    if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN && sendResult != FFmpegApi.AVERROR.EOF)
                    {
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet failed: {sendResult}");
                        return sendResult;
                    }
                    
                    int receiveResult = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                    
                    if (receiveResult < 0 && receiveResult != FFmpegApi.AVERROR.EAGAIN && receiveResult != FFmpegApi.AVERROR.EOF)
                    {
                        byte* errorBuffer = stackalloc byte[256];
                        if (FFmpegApi.av_strerror(receiveResult, errorBuffer, 256) == 0)
                        {
                            string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame failed: {errorMsg} (code: {receiveResult})");
                        }
                    }
                    
                    if (receiveResult == 0)
                    {
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                            $"Software decode completed successfully. Frame: Width={output.Frame->Width}, Height={output.Frame->Height}, " +
                            $"Format={output.Frame->Format}, Linesize0={output.Frame->LineSize[0]}");
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
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Exception in DecodeFrameSoftware: {ex.Message}\n{ex.StackTrace}");
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

    if (_mediaCodecCtx != null)
    {
        FFmpegApi.av_mediacodec_default_free(_context);
        _mediaCodecCtx = null;
    }

    if (_hwFrameCtx != null)
    {
        // 修复：创建局部指针变量并取其地址
        AVBufferRef* hwFrameCtxLocal = _hwFrameCtx;
        FFmpegApi.av_buffer_unref(&hwFrameCtxLocal);
        _hwFrameCtx = null;
    }

    if (_hwDeviceCtx != IntPtr.Zero)
    {
        fixed (IntPtr* ppRef = &_hwDeviceCtx)
        {
            FFmpegApi.av_buffer_unref(ppRef);
        }
        _hwDeviceCtx = IntPtr.Zero;
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
