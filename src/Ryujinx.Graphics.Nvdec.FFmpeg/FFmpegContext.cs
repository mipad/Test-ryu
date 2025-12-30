using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class FFmpegContext : IDisposable
    {
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private IntPtr _hwDeviceCtx;
        private AVFrame* _hwFrame;
        private AVFrame* _swFrame;
        
        // 参考hw_decode.c中的全局变量
        private static FFmpegApi.AVPixelFormat _hwPixelFormat = FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE;
        
        // 添加缺少的_decodeLock变量
        private object _decodeLock = new object();
        
        // 硬件解码器名称常量
        private const string H264MediaCodecDecoder = "h264_mediacodec";
        private const string VP8MediaCodecDecoder = "vp8_mediacodec";

        public FFmpegContext(string hardwareDecoderName)
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, $"FFmpegContext constructor called for hardware decoder: {hardwareDecoderName}");
            
            // 只尝试硬件解码，不进行软件解码回退
            if (!InitializeHardwareDecoder(hardwareDecoderName))
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder initialization failed for: {hardwareDecoderName}");
                throw new InvalidOperationException($"Failed to initialize hardware decoder: {hardwareDecoderName}");
            }
        }
        
        private bool InitializeHardwareDecoder(string decoderName)
        {
            try
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Initializing hardware decoder: {decoderName}");
                
                // 1. 查找硬件解码器
                _codec = FFmpegApi.avcodec_find_decoder_by_name(decoderName);
                if (_codec == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Hardware decoder not found: {decoderName}");
                    return false;
                }
                
                string foundCodecName = Marshal.PtrToStringUTF8((IntPtr)_codec->Name) ?? "unknown";
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found hardware decoder: {foundCodecName}");

                // 2. 查找硬件设备类型
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Finding MediaCodec device type...");
                FFmpegApi.AVHWDeviceType hwDeviceType = FindMediaCodecDeviceType();
                if (hwDeviceType == FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "MediaCodec device type not supported");
                    return false;
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Found MediaCodec device type: {hwDeviceType}");

                // 3. 查找硬件配置 - 参考hw_decode.c中的循环
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Finding hardware configuration...");
                if (!FindHardwareConfig(hwDeviceType))
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "No suitable hardware configuration found");
                    return false;
                }

                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Allocating codec context...");
                _context = FFmpegApi.avcodec_alloc_context3(_codec);
                if (_context == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate codec context");
                    return false;
                }

                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Allocated codec context: 0x{(ulong)_context:X}");

                // 4. 设置解码器参数
                _context->ThreadCount = 1; // 单线程解码，硬件解码通常不需要多线程
                _context->ThreadType = 0;
                _context->Flags |= 0x0001; // CODEC_FLAG_LOW_DELAY
                _context->Flags2 |= 0x00000100; // AV_CODEC_FLAG2_FAST

                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Set decoder params: ThreadCount={_context->ThreadCount}, Flags={_context->Flags}, Flags2={_context->Flags2}");

                // 5. 初始化硬件解码器 - 参考hw_decode.c中的hw_decoder_init
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Initializing hardware decoder...");
                if (InitHardwareDecoder(hwDeviceType) < 0)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to initialize hardware decoder");
                    return false;
                }

                // 6. 打开编解码器
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Opening codec...");
                int openResult = FFmpegApi.avcodec_open2(_context, _codec, null);
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"avcodec_open2 result: {openResult}");
                
                if (openResult < 0)
                {
                    byte* errorBuffer = stackalloc byte[256];
                    if (FFmpegApi.av_strerror(openResult, errorBuffer, 256) == 0)
                    {
                        string errorMsg = Marshal.PtrToStringUTF8((IntPtr)errorBuffer) ?? "Unknown error";
                        Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                            $"Failed to open hardware codec: {errorMsg} (code: {openResult})");
                    }
                    return false;
                }

                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Hardware codec opened successfully. Pixel format: {_context->PixFmt}, " +
                    $"Hardware pixel format: {_hwPixelFormat}, " +
                    $"HwDeviceCtx: 0x{(ulong)_context->HwDeviceCtx:X}");

                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Allocating packet...");
                _packet = FFmpegApi.av_packet_alloc();
                if (_packet == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate packet");
                    return false;
                }

                // 分配硬件帧和软件帧 - 参考hw_decode.c中的decode_write
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Allocating hardware and software frames...");
                _hwFrame = FFmpegApi.av_frame_alloc();
                _swFrame = FFmpegApi.av_frame_alloc();
                
                if (_hwFrame == null || _swFrame == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to allocate frames");
                    return false;
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Allocated hardware frame: 0x{(ulong)_hwFrame:X}, software frame: 0x{(ulong)_swFrame:X}");

                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Hardware decoder initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Exception initializing hardware decoder: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private FFmpegApi.AVHWDeviceType FindMediaCodecDeviceType()
        {
            // 查找MediaCodec设备类型
            FFmpegApi.AVHWDeviceType type = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            FFmpegApi.AVHWDeviceType prev = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            
            while ((type = FFmpegApi.av_hwdevice_iterate_types(prev)) != FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                string typeName = FFmpegApi.av_hwdevice_get_type_name(type) ?? "";
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"Found hardware device type: {typeName}");
                
                if (type == FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC)
                {
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, "Found MediaCodec hardware device type");
                    return type;
                }
                prev = type;
            }
            
            Logger.Error?.PrintMsg(LogClass.FFmpeg, "MediaCodec hardware device type not found");
            return FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        }

        private bool FindHardwareConfig(FFmpegApi.AVHWDeviceType hwDeviceType)
        {
            // 参考hw_decode.c中的硬件配置查找循环
            for (int i = 0;; i++)
            {
                IntPtr hwConfigPtr = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (hwConfigPtr == IntPtr.Zero)
                {
                    Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"No more hardware configs at index {i}");
                    break;
                }
                
                var hwConfig = (AVCodecHWConfig*)hwConfigPtr;
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                    $"Hardware config[{i}]: PixFmt={hwConfig->PixFmt}, " +
                    $"Methods={hwConfig->Methods}, DeviceType={hwConfig->DeviceType}");
                
                // 参考hw_decode.c中的条件检查
                if ((hwConfig->Methods & FFmpegApi.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 &&
                    hwConfig->DeviceType == (int)hwDeviceType)
                {
                    _hwPixelFormat = (FFmpegApi.AVPixelFormat)hwConfig->PixFmt;
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                        $"Found matching hardware config: PixelFormat={_hwPixelFormat}");
                    return true;
                }
            }
            
            return false;
        }

        // 参考hw_decode.c中的hw_decoder_init函数
        private int InitHardwareDecoder(FFmpegApi.AVHWDeviceType hwDeviceType)
        {
            try
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, "Initializing hardware decoder...");
                
                // 创建硬件设备上下文 - 参考hw_decode.c
                int result;
                fixed (IntPtr* hwDeviceCtxPtr = &_hwDeviceCtx)
                {
                    result = FFmpegApi.av_hwdevice_ctx_create(
                        hwDeviceCtxPtr, 
                        hwDeviceType, 
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
                    return result;
                }
                
                // 设置硬件设备上下文到编解码器上下文 - 参考hw_decode.c
                // 注意：C#中不能直接调用av_buffer_ref，但可以直接赋值
                _context->HwDeviceCtx = (AVBufferRef*)_hwDeviceCtx;
                
                if (_context->HwDeviceCtx == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to set hardware device context");
                    return -1;
                }
                
                Logger.Info?.PrintMsg(LogClass.FFmpeg, 
                    $"Set hardware device context: 0x{(ulong)_context->HwDeviceCtx:X}");
                
                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                    $"Exception in InitHardwareDecoder: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
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

        // 参考hw_decode.c中的decode_write函数
        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            lock (_decodeLock)
            {
                Logger.Debug?.PrintMsg(LogClass.FFmpeg, 
                    $"DecodeFrame called. Bitstream size: {bitstream.Length}");
                
                if (_context == null || _packet == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Decoder not initialized");
                    return -1;
                }
                
                if (_hwFrame == null || _swFrame == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Frames not allocated");
                    return -1;
                }

                // 清空帧
                FFmpegApi.av_frame_unref(_hwFrame);
                FFmpegApi.av_frame_unref(_swFrame);
                FFmpegApi.av_frame_unref(output.Frame);

                try
                {
                    fixed (byte* ptr = bitstream)
                    {
                        // 设置数据包
                        _packet->Data = ptr;
                        _packet->Size = bitstream.Length;
                        
                        // 发送数据包
                        int sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_send_packet result: {sendResult}");
                        
                        if (sendResult < 0 && sendResult != FFmpegApi.AVERROR.EAGAIN)
                        {
                            return sendResult;
                        }
                        
                        // 清空数据包
                        _packet->Data = null;
                        _packet->Size = 0;
                        FFmpegApi.av_packet_unref(_packet);
                        
                        // 接收帧
                        int receiveResult = FFmpegApi.avcodec_receive_frame(_context, _hwFrame);
                        Logger.Debug?.PrintMsg(LogClass.FFmpeg, $"avcodec_receive_frame result: {receiveResult}");
                        
                        if (receiveResult < 0)
                        {
                            if (receiveResult == FFmpegApi.AVERROR.EAGAIN)
                                return -1;
                            else if (receiveResult == FFmpegApi.AVERROR.EOF)
                                return 0;
                            else
                                return receiveResult;
                        }
                        
                        // 检查帧格式
                        AVFrame* tmpFrame;
                        
                        if (_hwFrame->Format == (int)_hwPixelFormat)
                        {
                            // 硬件帧，需要传输到系统内存
                            int transferResult = FFmpegApi.av_hwframe_transfer_data(_swFrame, _hwFrame, 0);
                            if (transferResult < 0)
                            {
                                Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                                    $"Failed to transfer data from hardware: {transferResult}");
                                return transferResult;
                            }
                            
                            tmpFrame = _swFrame;
                        }
                        else
                        {
                            // 帧已经在系统内存中
                            tmpFrame = _hwFrame;
                        }
                        
                        // 使用Surface处理帧转换
                        if (!output.TransferFromHardwareFrame(tmpFrame))
                        {
                            Logger.Error?.PrintMsg(LogClass.FFmpeg, "Failed to transfer frame to output surface");
                            return -1;
                        }
                        
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, 
                        $"Exception in DecodeFrame: {ex.Message}\n{ex.StackTrace}");
                    return -1;
                }
            }
        }

        public void Dispose()
        {
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "Disposing FFmpegContext");
            
            // 释放帧
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

            // 释放硬件设备上下文
            if (_hwDeviceCtx != IntPtr.Zero)
            {
                fixed (IntPtr* hwDeviceCtxPtr = &_hwDeviceCtx)
                {
                    FFmpegApi.av_buffer_unref(hwDeviceCtxPtr);
                }
                _hwDeviceCtx = IntPtr.Zero;
            }

            // 释放数据包
            if (_packet != null)
            {
                fixed (AVPacket** packetPtr = &_packet)
                {
                    FFmpegApi.av_packet_free(packetPtr);
                }
            }

            // 释放编解码器上下文
            if (_context != null)
            {
                FFmpegApi.avcodec_close(_context);
                
                fixed (AVCodecContext** contextPtr = &_context)
                {
                    FFmpegApi.avcodec_free_context(contextPtr);
                }
            }
            
            Logger.Info?.PrintMsg(LogClass.FFmpeg, "FFmpegContext disposed");
        }
    }
}
