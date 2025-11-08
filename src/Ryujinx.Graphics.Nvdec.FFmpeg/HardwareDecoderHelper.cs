using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    unsafe class HardwareDecoderHelper : IDisposable
    {
        private AVCodec* _codec;
        private AVCodecContext* _context;
        private bool _isInitialized = false;
        
        public bool IsAvailable { get; private set; }
        public string CodecName { get; private set; }
        public string ErrorMessage { get; private set; }

        public HardwareDecoderHelper(AVCodecID codecId, string hardwareDecoderName)
        {
            Initialize(codecId, hardwareDecoderName);
        }

        private void Initialize(AVCodecID codecId, string hardwareDecoderName)
        {
            try
            {
                Logger.Info?.Print(LogClass.FFmpeg, $"Initializing hardware decoder helper for {codecId} with {hardwareDecoderName}");

                // 尝试找到硬件解码器
                _codec = FFmpegApi.avcodec_find_decoder_by_name(hardwareDecoderName);
                if (_codec == null)
                {
                    ErrorMessage = $"Hardware decoder {hardwareDecoderName} not found";
                    Logger.Warning?.Print(LogClass.FFmpeg, ErrorMessage);
                    return;
                }

                CodecName = Marshal.PtrToStringAnsi((IntPtr)_codec->Name) ?? hardwareDecoderName;
                Logger.Debug?.Print(LogClass.FFmpeg, $"Found hardware decoder: {CodecName}");

                // 创建编解码器上下文
                _context = FFmpegApi.avcodec_alloc_context3(_codec);
                if (_context == null)
                {
                    ErrorMessage = "Failed to allocate codec context for hardware decoder";
                    Logger.Error?.Print(LogClass.FFmpeg, ErrorMessage);
                    return;
                }

                // 配置硬件解码器参数
                ConfigureHardwareDecoder();

                // 尝试打开编解码器
                int result = FFmpegApi.avcodec_open2(_context, _codec, null);
                if (result != 0)
                {
                    ErrorMessage = $"Failed to open hardware decoder: {GetFFmpegErrorDescription(result)}";
                    Logger.Error?.Print(LogClass.FFmpeg, ErrorMessage);
                    
                    // 清理资源
                    fixed (AVCodecContext** ppContext = &_context)
                    {
                        FFmpegApi.avcodec_free_context(ppContext);
                    }
                    return;
                }

                _isInitialized = true;
                IsAvailable = true;
                Logger.Info?.Print(LogClass.FFmpeg, $"Hardware decoder {CodecName} initialized successfully");
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Exception initializing hardware decoder: {ex.Message}";
                Logger.Error?.Print(LogClass.FFmpeg, ErrorMessage);
            }
        }

        private void ConfigureHardwareDecoder()
        {
            // 硬件解码器特定配置
            _context->ThreadCount = 1; // 硬件解码器通常单线程
            _context->Flags2 |= 0x00000001; // CODEC_FLAG2_FAST
            _context->ErrRecognition = 0x0001; // 基本错误识别
            
            // 设置合理的默认值
            _context->Width = 1920;
            _context->Height = 1080;
            _context->Profile = 100; // H.264 High Profile
            _context->Level = 40; // Level 4.0

            Logger.Debug?.Print(LogClass.FFmpeg, "Configured hardware decoder with optimized settings");
        }

        private string GetFFmpegErrorDescription(int errorCode)
        {
            switch (errorCode)
            {
                case -1: return "Generic error (check MediaCodec availability)";
                case -2: return "Codec not found";
                case -3: return "Invalid data";
                case -4: return "Buffer too small";
                case -5: return "End of file";
                case -11: return "Resource temporarily unavailable";
                case -1094995529: return "Invalid data (specific)";
                case -1313558101: return "Unknown error (hardware specific)";
                case -541478725: return "End of file (specific)";
                default: return $"Error code: {errorCode}";
            }
        }

        public AVCodecContext* GetContext()
        {
            return _isInitialized ? _context : null;
        }

        public void Dispose()
        {
            if (_isInitialized && _context != null)
            {
                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
                _isInitialized = false;
                IsAvailable = false;
                Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder helper disposed");
            }
        }
    }
}