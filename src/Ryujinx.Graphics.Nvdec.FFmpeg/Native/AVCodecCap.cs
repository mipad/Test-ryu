namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
enum AVCodecCap
    {
        AV_CODEC_CAP_DRAW_HORIZ_BAND = 1 << 0,
        AV_CODEC_CAP_DR1 = 1 << 1,
        AV_CODEC_CAP_TRUNCATED = 1 << 3,
        AV_CODEC_CAP_DELAY = 1 << 5,
        AV_CODEC_CAP_SMALL_LAST_FRAME = 1 << 6,
        AV_CODEC_CAP_SUBFRAMES = 1 << 8,
        AV_CODEC_CAP_EXPERIMENTAL = 1 << 9,
        AV_CODEC_CAP_CHANNEL_CONF = 1 << 10,
        AV_CODEC_CAP_FRAME_THREADS = 1 << 12,
        AV_CODEC_CAP_SLICE_THREADS = 1 << 13,
        AV_CODEC_CAP_PARAM_CHANGE = 1 << 14,
        AV_CODEC_CAP_AUTO_THREADS = 1 << 15,
        AV_CODEC_CAP_VARIABLE_FRAME_SIZE = 1 << 16,
        AV_CODEC_CAP_AVOID_PROBING = 1 << 17,
        AV_CODEC_CAP_HARDWARE = 1 << 18,
        AV_CODEC_CAP_HYBRID = 1 << 19,
        AV_CODEC_CAP_ENCODER_REORDERED_OPAQUE = 1 << 20,
    }
}
