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

        private readonly AVCodec_decode _decodeFrame;
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private readonly AVCodec* _codec;
        private readonly AVPacket* _packet;
        private readonly AVCodecContext* _context;
        private readonly bool _useHardwareDecoding;
        private AVFrame* _hwFrame;
        private AVFrame* _swFrame;

        public FFmpegContext(AVCodecID codecId)
        {
            // 检查是否应该使用硬件解码
            _useHardwareDecoding = ShouldUseHardwareDecoding(codecId);
            
            _codec = FFmpegApi.avcodec_find_decoder(codecId);
            if (_codec == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                return;
            }

            _context = FFmpegApi.avcodec_alloc_context3(_codec);
            if (_context == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                return;
            }

            // 如果支持硬件解码，尝试初始化硬件解码器
            if (_useHardwareDecoding && TryInitializeHardwareDecoder())
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using hardware decoder for {codecId}");
            }
            else
            {
                Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Using software decoder for {codecId}");
            }

            if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                return;
            }

            _packet = FFmpegApi.av_packet_alloc();
            if (_packet == null)
            {
                Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                return;
            }

            // 如果需要硬件解码，创建硬件帧
            if (_useHardwareDecoding && _context->HwDeviceCtx != null)
            {
                _hwFrame = FFmpegApi.av_frame_alloc();
                _swFrame = FFmpegApi.av_frame_alloc();
            }

            int avCodecRawVersion = FFmpegApi.avcodec_version();
            int avCodecMajorVersion = avCodecRawVersion >> 16;
            int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;

            // libavcodec 59.24 changed AvCodec to move its private API and also move the codec function to an union.
            if (avCodecMajorVersion > 59 || (avCodecMajorVersion == 59 && avCodecMinorVersion > 24))
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodec<AVCodec>*)_codec)->CodecCallback);
            }
            // libavcodec 59.x changed AvCodec private API layout.
            else if (avCodecMajorVersion == 59)
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec501>*)_codec)->Decode);
            }
            // libavcodec 58.x and lower
            else
            {
                _decodeFrame = Marshal.GetDelegateForFunctionPointer<AVCodec_decode>(((FFCodecLegacy<AVCodec>*)_codec)->Decode);
            }
        }

        private bool ShouldUseHardwareDecoding(AVCodecID codecId)
        {
            // 仅在Android平台上为H264和VP8启用硬件解码
            if (!OperatingSystem.IsAndroid())
                return false;
                
            // 只支持H264和VP8
            if (codecId != AVCodecID.AV_CODEC_ID_H264 && codecId != AVCodecID.AV_CODEC_ID_VP8)
                return false;
                
            return true;
        }

        private bool TryInitializeHardwareDecoder()
        {
            // 尝试查找MediaCodec硬件解码器
            AVHWDeviceType deviceType = FFmpegApi.AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC;
            
            // 检查解码器是否支持硬件解码
            AVCodecHWConfig* hwConfig = null;
            for (int i = 0; ; i++)
            {
                hwConfig = FFmpegApi.avcodec_get_hw_config(_codec, i);
                if (hwConfig == null)
                    break;
                    
                if (hwConfig->DeviceType == deviceType && 
                    (hwConfig->Methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
                {
                    // 创建硬件设备上下文
                    AVBufferRef* hwDeviceCtx = FFmpegApi.av_hwdevice_ctx_alloc(deviceType);
                    if (hwDeviceCtx == null)
                    {
                        Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to allocate hardware device context");
                        return false;
                    }
                    
                    if (FFmpegApi.av_hwdevice_ctx_init(hwDeviceCtx) < 0)
                    {
                        Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Failed to initialize hardware device context");
                        FFmpegApi.av_buffer_unref(&hwDeviceCtx);
                        return false;
                    }
                    
                    // 设置硬件设备上下文
                    _context->HwDeviceCtx = hwDeviceCtx;
                    _context->PixFmt = (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
                    
                    // 设置get_format回调
                    _context->GetFormat = (IntPtr)Marshal.GetFunctionPointerForDelegate(
                        new GetFormatDelegate(GetHardwareFormat));
                    
                    Logger.Info?.PrintMsg(LogClass.FFmpeg, $"Initialized MediaCodec hardware decoder");
                    return true;
                }
            }
            
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "No MediaCodec hardware configuration found");
            return false;
        }
        
        private delegate int GetFormatDelegate(AVCodecContext* ctx, FFmpegApi.AVPixelFormat* pix_fmts);
        
        private int GetHardwareFormat(AVCodecContext* ctx, FFmpegApi.AVPixelFormat* pix_fmts)
        {
            // 查找支持的像素格式
            for (FFmpegApi.AVPixelFormat* p = pix_fmts; *p != FFmpegApi.AVPixelFormat.AV_PIX_FMT_NONE; p++)
            {
                if (*p == FFmpegApi.AVPixelFormat.AV_PIX_FMT_MEDIACODEC)
                {
                    return (int)*p;
                }
            }
            
            // 如果没有找到硬件格式，回退到软件解码
            Logger.Warning?.PrintMsg(LogClass.FFmpeg, "Hardware pixel format not found, falling back to software");
            FFmpegApi.av_buffer_unref(&ctx->HwDeviceCtx);
            return (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P;
        }

        static FFmpegContext()
        {
            _logFunc = Log;

            // Redirect log output.
            FFmpegApi.av_log_set_level(AVLog.MaxOffset);
            FFmpegApi.av_log_set_callback(_logFunc);
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
            if (_useHardwareDecoding && _context->HwDeviceCtx != null)
            {
                return DecodeFrameHardware(output, bitstream);
            }
            else
            {
                return DecodeFrameSoftware(output, bitstream);
            }
        }
        
        private int DecodeFrameHardware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            FFmpegApi.av_frame_unref(_hwFrame);
            FFmpegApi.av_frame_unref(_swFrame);
            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
            }

            if (gotFrame == 0)
            {
                // 如果帧未送达，可能是延迟的
                // 通过传递0长度包获取下一个延迟帧
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, _hwFrame, &gotFrame, _packet);
                
                // 将B帧设置为0，因为我们已经消耗了所有延迟帧
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                return -1;
            }

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
            
            return result < 0 ? result : 0;
        }
        
        private int DecodeFrameSoftware(Surface output, ReadOnlySpan<byte> bitstream)
        {
            FFmpegApi.av_frame_unref(output.Frame);

            int result;
            int gotFrame;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);
            }

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);

                // 如果帧未送达，可能是延迟的
                // 通过传递0长度包获取下一个延迟帧
                _packet->Data = null;
                _packet->Size = 0;
                result = _decodeFrame(_context, output.Frame, &gotFrame, _packet);

                // 将B帧设置为0，因为我们已经消耗了所有延迟帧
                _context->HasBFrames = 0;
            }

            FFmpegApi.av_packet_unref(_packet);

            if (gotFrame == 0)
            {
                FFmpegApi.av_frame_unref(output.Frame);
                return -1;
            }

            return result < 0 ? result : 0;
        }
        
        private unsafe void CopyFrameData(AVFrame* src, AVFrame* dst)
        {
            // 复制基本属性
            dst->Width = src->Width;
            dst->Height = src->Height;
            dst->Format = src->Format;
            dst->InterlacedFrame = src->InterlacedFrame;
            
            // 复制行大小
            for (int i = 0; i < 4; i++)
            {
                dst->LineSize[i] = src->LineSize[i];
            }
            
            // 复制数据指针
            for (int i = 0; i < 4; i++)
            {
                dst->Data[i] = src->Data[i];
            }
            
            // 对于YUV420P格式，我们需要确保正确的平面排列
            if (src->Format == (int)FFmpegApi.AVPixelFormat.AV_PIX_FMT_YUV420P)
            {
                // YUV420P格式已经正确处理
            }
        }

        public void Dispose()
        {
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
        }
    }
}