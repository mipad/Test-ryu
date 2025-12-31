// [file name]: AndroidHardwareDecoder.cs
namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    /// <summary>
    /// Android平台专用的硬件解码器配置
    /// </summary>
    public static class AndroidHardwareDecoder
    {
        /// <summary>
        /// Android MediaCodec 解码器名称映射
        /// </summary>
        public static readonly Dictionary<AVCodecID, string> MediaCodecDecoders = new()
        {
            { AVCodecID.AV_CODEC_ID_H264, "h264_mediacodec" },
            { AVCodecID.AV_CODEC_ID_VP8, "vp8_mediacodec" },
            { AVCodecID.AV_CODEC_ID_HEVC, "hevc_mediacodec" },
            { AVCodecID.AV_CODEC_ID_VP9, "vp9_mediacodec" },
            { AVCodecID.AV_CODEC_ID_AV1, "av1_mediacodec" },
        };

        /// <summary>
        /// 检查特定编码格式是否支持MediaCodec硬件解码
        /// </summary>
        public static bool IsSupportedByMediaCodec(AVCodecID codecId)
        {
            return MediaCodecDecoders.ContainsKey(codecId);
        }

        /// <summary>
        /// 获取MediaCodec解码器名称
        /// </summary>
        public static string GetMediaCodecDecoderName(AVCodecID codecId)
        {
            return MediaCodecDecoders.GetValueOrDefault(codecId);
        }

        /// <summary>
        /// 尝试查找MediaCodec硬件解码器
        /// </summary>
        public static unsafe IntPtr FindMediaCodecDecoder(AVCodecID codecId)
        {
            if (!IsSupportedByMediaCodec(codecId))
                return IntPtr.Zero;

            var decoderName = GetMediaCodecDecoderName(codecId);
            if (string.IsNullOrEmpty(decoderName))
                return IntPtr.Zero;

            // 使用 FFmpeg API 查找解码器
            return FFmpegApi.avcodec_find_decoder_by_name(decoderName);
        }
    }
}
