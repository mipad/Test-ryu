using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct AVCodecParameters
    {
        public AVMediaType codec_type;
        public AVCodecID codec_id;
        public uint codec_tag;
        public byte* extradata;
        public int extradata_size;
        public int format;
        public long bit_rate;
        public int bits_per_coded_sample;
        public int bits_per_raw_sample;
        public int profile;
        public int level;
        public int width;
        public int height;
        public AVRational sample_aspect_ratio;
        public AVFieldOrder field_order;
        public AVColorRange color_range;
        public AVColorPrimaries color_primaries;
        public AVColorTransferCharacteristic color_trc;
        public AVColorSpace color_space;
        public AVChromaLocation chroma_location;
        public int video_delay;
        public ulong channel_layout;
        public int channels;
        public int sample_rate;
        public int block_align;
        public int frame_size;
        public int initial_padding;
        public int trailing_padding;
        public int seek_preroll;
    }

    enum AVFieldOrder
    {
        AV_FIELD_UNKNOWN,
        AV_FIELD_PROGRESSIVE,
        AV_FIELD_TT,
        AV_FIELD_BB,
        AV_FIELD_TB,
        AV_FIELD_BT
    }

    enum AVColorRange
    {
        AVCOL_RANGE_UNSPECIFIED = 0,
        AVCOL_RANGE_MPEG = 1,
        AVCOL_RANGE_JPEG = 2,
        AVCOL_RANGE_NB
    }

    enum AVColorPrimaries
    {
        AVCOL_PRI_RESERVED0 = 0,
        AVCOL_PRI_BT709 = 1,
        AVCOL_PRI_UNSPECIFIED = 2,
        AVCOL_PRI_RESERVED = 3,
        AVCOL_PRI_BT470M = 4,
        AVCOL_PRI_BT470BG = 5,
        AVCOL_PRI_SMPTE170M = 6,
        AVCOL_PRI_SMPTE240M = 7,
        AVCOL_PRI_FILM = 8,
        AVCOL_PRI_BT2020 = 9,
        AVCOL_PRI_SMPTE428 = 10,
        AVCOL_PRI_SMPTEST428_1 = 10,
        AVCOL_PRI_SMPTE431 = 11,
        AVCOL_PRI_SMPTE432 = 12,
        AVCOL_PRI_EBU3213 = 22,
        AVCOL_PRI_NB
    }

    enum AVColorTransferCharacteristic
    {
        AVCOL_TRC_RESERVED0 = 0,
        AVCOL_TRC_BT709 = 1,
        AVCOL_TRC_UNSPECIFIED = 2,
        AVCOL_TRC_RESERVED = 3,
        AVCOL_TRC_GAMMA22 = 4,
        AVCOL_TRC_GAMMA28 = 5,
        AVCOL_TRC_SMPTE170M = 6,
        AVCOL_TRC_SMPTE240M = 7,
        AVCOL_TRC_LINEAR = 8,
        AVCOL_TRC_LOG = 9,
        AVCOL_TRC_LOG_SQRT = 10,
        AVCOL_TRC_IEC61966_2_4 = 11,
        AVCOL_TRC_BT1361_ECG = 12,
        AVCOL_TRC_IEC61966_2_1 = 13,
        AVCOL_TRC_BT2020_10 = 14,
        AVCOL_TRC_BT2020_12 = 15,
        AVCOL_TRC_SMPTE2084 = 16,
        AVCOL_TRC_SMPTEST2084 = 16,
        AVCOL_TRC_SMPTE428 = 17,
        AVCOL_TRC_SMPTEST428_1 = 17,
        AVCOL_TRC_ARIB_STD_B67 = 18,
        AVCOL_TRC_NB
    }

    enum AVColorSpace
    {
        AVCOL_SPC_RGB = 0,
        AVCOL_SPC_BT709 = 1,
        AVCOL_SPC_UNSPECIFIED = 2,
        AVCOL_SPC_RESERVED = 3,
        AVCOL_SPC_FCC = 4,
        AVCOL_SPC_BT470BG = 5,
        AVCOL_SPC_SMPTE170M = 6,
        AVCOL_SPC_SMPTE240M = 7,
        AVCOL_SPC_YCGCO = 8,
        AVCOL_SPC_YCOCG = 8,
        AVCOL_SPC_BT2020_NCL = 9,
        AVCOL_SPC_BT2020_CL = 10,
        AVCOL_SPC_SMPTE2085 = 11,
        AVCOL_SPC_CHROMA_DERIVED_NCL = 12,
        AVCOL_SPC_CHROMA_DERIVED_CL = 13,
        AVCOL_SPC_ICTCP = 14,
        AVCOL_SPC_NB
    }

    enum AVChromaLocation
    {
        AVCHROMA_LOC_UNSPECIFIED = 0,
        AVCHROMA_LOC_LEFT = 1,
        AVCHROMA_LOC_CENTER = 2,
        AVCHROMA_LOC_TOPLEFT = 3,
        AVCHROMA_LOC_TOP = 4,
        AVCHROMA_LOC_BOTTOMLEFT = 5,
        AVCHROMA_LOC_BOTTOM = 6,
        AVCHROMA_LOC_NB
    }
}
