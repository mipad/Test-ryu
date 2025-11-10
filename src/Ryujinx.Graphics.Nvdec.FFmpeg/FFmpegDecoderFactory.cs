using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    /// <summary>
    /// FFmpeg 解码器工厂类
    /// </summary>
    public static class FFmpegDecoderFactory
    {
        /// <summary>
        /// 创建合适的解码器（优先硬件解码）
        /// </summary>
        public static IVideoDecoder CreateDecoder(AVCodecID codecId, bool preferHardware = true)
        {
            string codecName = GetCodecNameFromId(codecId);
            
            Logger.Info?.Print(LogClass.FFmpeg, 
                $"Creating decoder for {codecName}, hardware preference: {preferHardware}");

            if (preferHardware)
            {
                try
                {
                    // 检查硬件解码支持
                    if (FFmpegHardwareDecoder.IsMediaCodecSupported() && 
                        FFmpegHardwareDecoder.IsCodecHardwareSupported(codecName))
                    {
                        var hardwareDecoder = new FFmpegHardwareDecoder(codecName);
                        if (hardwareDecoder.IsInitialized)
                        {
                            Logger.Info?.Print(LogClass.FFmpeg, 
                                $"Using hardware decoder for {codecName}");
                            return hardwareDecoder;
                        }
                    }
                    
                    Logger.Info?.Print(LogClass.FFmpeg, 
                        $"Hardware decoder not available for {codecName}, falling back to software");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.FFmpeg, 
                        $"Hardware decoder creation failed for {codecName}: {ex.Message}, falling back to software");
                }
            }

            // 回退到软件解码器
            try
            {
                var softwareDecoder = new FFmpegContext(codecId, false); // 明确不使用硬件解码
                if (softwareDecoder.IsInitialized)
                {
                    Logger.Info?.Print(LogClass.FFmpeg, 
                        $"Using software decoder for {codecName}");
                    return softwareDecoder;
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.FFmpeg, 
                    $"Software decoder creation also failed for {codecName}: {ex.Message}");
            }

            Logger.Error?.Print(LogClass.FFmpeg, 
                $"No suitable decoder found for {codecName}");
            return null;
        }

        /// <summary>
        /// 获取解码器信息
        /// </summary>
        public static string GetDecoderInfo()
        {
            try
            {
                string ffmpegVersion = FFmpegHardwareDecoder.GetVersionInfo();
                bool mediaCodecSupported = FFmpegHardwareDecoder.IsMediaCodecSupported();
                
                return $"FFmpeg: {ffmpegVersion}, MediaCodec Supported: {mediaCodecSupported}";
            }
            catch (Exception ex)
            {
                return $"Failed to get decoder info: {ex.Message}";
            }
        }

        private static string GetCodecNameFromId(AVCodecID codecId)
        {
            switch (codecId)
            {
                case AVCodecID.AV_CODEC_ID_H264: return "h264";
                case AVCodecID.AV_CODEC_ID_HEVC: return "hevc";
                case AVCodecID.AV_CODEC_ID_VP8: return "vp8";
                case AVCodecID.AV_CODEC_ID_VP9: return "vp9";
                case AVCodecID.AV_CODEC_ID_AV1: return "av1";
                default: return codecId.ToString().ToLower();
            }
        }
    }
}