using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    static partial class FFmpegApi
    {
        public const string AvCodecLibraryName = "avcodec";
        public const string AvUtilLibraryName = "avutil";

        private static readonly Dictionary<string, (int, int)> _librariesWhitelist = new()
        {
            { AvCodecLibraryName, (59, 61) },
            { AvUtilLibraryName, (57, 59) },
        };

        private static string FormatLibraryNameForCurrentOs(string libraryName, int version)
        {
            // Android 使用 .so 文件
            return $"lib{libraryName}.so.{version}";
        }

        private static bool TryLoadWhitelistedLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath, out IntPtr handle)
        {
            handle = IntPtr.Zero;

            if (_librariesWhitelist.TryGetValue(libraryName, out var value))
            {
                (int minVersion, int maxVersion) = value;

                for (int version = maxVersion; version >= minVersion; version--)
                {
                    if (NativeLibrary.TryLoad(FormatLibraryNameForCurrentOs(libraryName, version), assembly, searchPath, out handle))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static FFmpegApi()
        {
            NativeLibrary.SetDllImportResolver(typeof(FFmpegApi).Assembly, (name, assembly, path) =>
            {
                if (name == AvUtilLibraryName && TryLoadWhitelistedLibrary(AvUtilLibraryName, assembly, path, out nint handle))
                {
                    return handle;
                }
                else if (name == AvCodecLibraryName && TryLoadWhitelistedLibrary(AvCodecLibraryName, assembly, path, out handle))
                {
                    return handle;
                }

                return IntPtr.Zero;
            });
        }

        public unsafe delegate void av_log_set_callback_callback(void* a0, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string a2, byte* a3);

        // 硬件设备类型枚举
        public enum AVHWDeviceType
        {
            AV_HWDEVICE_TYPE_NONE = 0,
            AV_HWDEVICE_TYPE_VDPAU = 1,
            AV_HWDEVICE_TYPE_CUDA = 2,
            AV_HWDEVICE_TYPE_VAAPI = 3,
            AV_HWDEVICE_TYPE_DXVA2 = 4,
            AV_HWDEVICE_TYPE_QSV = 5,
            AV_HWDEVICE_TYPE_VIDEOTOOLBOX = 6,
            AV_HWDEVICE_TYPE_D3D11VA = 7,
            AV_HWDEVICE_TYPE_DRM = 8,
            AV_HWDEVICE_TYPE_OPENCL = 9,
            AV_HWDEVICE_TYPE_MEDIACODEC = 10,  // Android 硬件解码
            AV_HWDEVICE_TYPE_VULKAN = 11,
            AV_HWDEVICE_TYPE_D3D12VA = 12,
        }

        // 像素格式枚举
        public enum AVPixelFormat
        {
            AV_PIX_FMT_NONE = -1,
            AV_PIX_FMT_YUV420P = 0,
            AV_PIX_FMT_YUYV422 = 1,
            AV_PIX_FMT_RGB24 = 2,
            AV_PIX_FMT_BGR24 = 3,
            AV_PIX_FMT_YUV422P = 4,
            AV_PIX_FMT_YUV444P = 5,
            AV_PIX_FMT_YUV410P = 6,
            AV_PIX_FMT_YUV411P = 7,
            AV_PIX_FMT_GRAY8 = 8,
            AV_PIX_FMT_MONOWHITE = 9,
            AV_PIX_FMT_MONOBLACK = 10,
            AV_PIX_FMT_PAL8 = 11,
            AV_PIX_FMT_YUVJ420P = 12,
            AV_PIX_FMT_YUVJ422P = 13,
            AV_PIX_FMT_YUVJ444P = 14,
            AV_PIX_FMT_UYVY422 = 15,
            AV_PIX_FMT_UYYVYY411 = 16,
            AV_PIX_FMT_BGR8 = 17,
            AV_PIX_FMT_BGR4 = 18,
            AV_PIX_FMT_BGR4_BYTE = 19,
            AV_PIX_FMT_RGB8 = 20,
            AV_PIX_FMT_RGB4 = 21,
            AV_PIX_FMT_RGB4_BYTE = 22,
            AV_PIX_FMT_NV12 = 23,
            AV_PIX_FMT_NV21 = 24,
            AV_PIX_FMT_ARGB = 25,
            AV_PIX_FMT_RGBA = 26,
            AV_PIX_FMT_ABGR = 27,
            AV_PIX_FMT_BGRA = 28,
            AV_PIX_FMT_GRAY16BE = 29,
            AV_PIX_FMT_GRAY16LE = 30,
            AV_PIX_FMT_YUV440P = 31,
            AV_PIX_FMT_YUVJ440P = 32,
            AV_PIX_FMT_YUVA420P = 33,
            AV_PIX_FMT_RGB48BE = 34,
            AV_PIX_FMT_RGB48LE = 35,
            AV_PIX_FMT_RGB565BE = 36,
            AV_PIX_FMT_RGB565LE = 37,
            AV_PIX_FMT_RGB555BE = 38,
            AV_PIX_FMT_RGB555LE = 39,
            AV_PIX_FMT_BGR565BE = 40,
            AV_PIX_FMT_BGR565LE = 41,
            AV_PIX_FMT_BGR555BE = 42,
            AV_PIX_FMT_BGR555LE = 43,
            AV_PIX_FMT_VAAPI = 44,
            AV_PIX_FMT_YUV420P16LE = 45,
            AV_PIX_FMT_YUV420P16BE = 46,
            AV_PIX_FMT_YUV422P16LE = 47,
            AV_PIX_FMT_YUV422P16BE = 48,
            AV_PIX_FMT_YUV444P16LE = 49,
            AV_PIX_FMT_YUV444P16BE = 50,
            AV_PIX_FMT_DXVA2_VLD = 51,
            AV_PIX_FMT_RGB444LE = 52,
            AV_PIX_FMT_RGB444BE = 53,
            AV_PIX_FMT_BGR444LE = 54,
            AV_PIX_FMT_BGR444BE = 55,
            AV_PIX_FMT_YA8 = 56,
            AV_PIX_FMT_Y400A = 57,
            AV_PIX_FMT_GRAY8A = 58,
            AV_PIX_FMT_BGR48BE = 59,
            AV_PIX_FMT_BGR48LE = 60,
            AV_PIX_FMT_YUV420P9BE = 61,
            AV_PIX_FMT_YUV420P9LE = 62,
            AV_PIX_FMT_YUV420P10BE = 63,
            AV_PIX_FMT_YUV420P10LE = 64,
            AV_PIX_FMT_YUV422P10BE = 65,
            AV_PIX_FMT_YUV422P10LE = 66,
            AV_PIX_FMT_YUV444P9BE = 67,
            AV_PIX_FMT_YUV444P9LE = 68,
            AV_PIX_FMT_YUV444P10BE = 69,
            AV_PIX_FMT_YUV444P10LE = 70,
            AV_PIX_FMT_YUV422P9BE = 71,
            AV_PIX_FMT_YUV422P9LE = 72,
            AV_PIX_FMT_GBRP = 73,
            AV_PIX_FMT_GBR24P = 74,
            AV_PIX_FMT_GBRP9BE = 75,
            AV_PIX_FMT_GBRP9LE = 76,
            AV_PIX_FMT_GBRP10BE = 77,
            AV_PIX_FMT_GBRP10LE = 78,
            AV_PIX_FMT_GBRP16BE = 79,
            AV_PIX_FMT_GBRP16LE = 80,
            AV_PIX_FMT_YUVA422P = 81,
            AV_PIX_FMT_YUVA444P = 82,
            AV_PIX_FMT_YUVA420P9BE = 83,
            AV_PIX_FMT_YUVA420P9LE = 84,
            AV_PIX_FMT_YUVA422P9BE = 85,
            AV_PIX_FMT_YUVA422P9LE = 86,
            AV_PIX_FMT_YUVA444P9BE = 87,
            AV_PIX_FMT_YUVA444P9LE = 88,
            AV_PIX_FMT_YUVA420P10BE = 89,
            AV_PIX_FMT_YUVA420P10LE = 90,
            AV_PIX_FMT_YUVA422P10BE = 91,
            AV_PIX_FMT_YUVA422P10LE = 92,
            AV_PIX_FMT_YUVA444P10BE = 93,
            AV_PIX_FMT_YUVA444P10LE = 94,
            AV_PIX_FMT_YUVA420P16BE = 95,
            AV_PIX_FMT_YUVA420P16LE = 96,
            AV_PIX_FMT_YUVA422P16BE = 97,
            AV_PIX_FMT_YUVA422P16LE = 98,
            AV_PIX_FMT_YUVA444P16BE = 99,
            AV_PIX_FMT_YUVA444P16LE = 100,
            AV_PIX_FMT_VDPAU = 101,
            AV_PIX_FMT_XYZ12LE = 102,
            AV_PIX_FMT_XYZ12BE = 103,
            AV_PIX_FMT_NV16 = 104,
            AV_PIX_FMT_NV20LE = 105,
            AV_PIX_FMT_NV20BE = 106,
            AV_PIX_FMT_RGBA64BE = 107,
            AV_PIX_FMT_RGBA64LE = 108,
            AV_PIX_FMT_BGRA64BE = 109,
            AV_PIX_FMT_BGRA64LE = 110,
            AV_PIX_FMT_YVYU422 = 111,
            AV_PIX_FMT_YA16BE = 112,
            AV_PIX_FMT_YA16LE = 113,
            AV_PIX_FMT_GBRAP = 114,
            AV_PIX_FMT_GBRAP16BE = 115,
            AV_PIX_FMT_GBRAP16LE = 116,
            AV_PIX_FMT_QSV = 117,
            AV_PIX_FMT_MMAL = 118,
            AV_PIX_FMT_D3D11VA_VLD = 119,
            AV_PIX_FMT_CUDA = 120,
            AV_PIX_FMT_0RGB = 121,
            AV_PIX_FMT_RGB0 = 122,
            AV_PIX_FMT_0BGR = 123,
            AV_PIX_FMT_BGR0 = 124,
            AV_PIX_FMT_YUV420P12BE = 125,
            AV_PIX_FMT_YUV420P12LE = 126,
            AV_PIX_FMT_YUV420P14BE = 127,
            AV_PIX_FMT_YUV420P14LE = 128,
            AV_PIX_FMT_YUV422P12BE = 129,
            AV_PIX_FMT_YUV422P12LE = 130,
            AV_PIX_FMT_YUV422P14BE = 131,
            AV_PIX_FMT_YUV422P14LE = 132,
            AV_PIX_FMT_YUV444P12BE = 133,
            AV_PIX_FMT_YUV444P12LE = 134,
            AV_PIX_FMT_YUV444P14BE = 135,
            AV_PIX_FMT_YUV444P14LE = 136,
            AV_PIX_FMT_GBRP12BE = 137,
            AV_PIX_FMT_GBRP12LE = 138,
            AV_PIX_FMT_GBRP14BE = 139,
            AV_PIX_FMT_GBRP14LE = 140,
            AV_PIX_FMT_YUVJ411P = 141,
            AV_PIX_FMT_BAYER_BGGR8 = 142,
            AV_PIX_FMT_BAYER_RGGB8 = 143,
            AV_PIX_FMT_BAYER_GBRG8 = 144,
            AV_PIX_FMT_BAYER_GRBG8 = 145,
            AV_PIX_FMT_BAYER_BGGR16LE = 146,
            AV_PIX_FMT_BAYER_BGGR16BE = 147,
            AV_PIX_FMT_BAYER_RGGB16LE = 148,
            AV_PIX_FMT_BAYER_RGGB16BE = 149,
            AV_PIX_FMT_BAYER_GBRG16LE = 150,
            AV_PIX_FMT_BAYER_GBRG16BE = 151,
            AV_PIX_FMT_BAYER_GRBG16LE = 152,
            AV_PIX_FMT_BAYER_GRBG16BE = 153,
            AV_PIX_FMT_XVMC = 154,
            AV_PIX_FMT_YUV440P10LE = 155,
            AV_PIX_FMT_YUV440P10BE = 156,
            AV_PIX_FMT_YUV440P12LE = 157,
            AV_PIX_FMT_YUV440P12BE = 158,
            AV_PIX_FMT_AYUV64LE = 159,
            AV_PIX_FMT_AYUV64BE = 160,
            AV_PIX_FMT_VIDEOTOOLBOX = 161,
            AV_PIX_FMT_P010LE = 162,
            AV_PIX_FMT_P010BE = 163,
            AV_PIX_FMT_GBRAP12BE = 164,
            AV_PIX_FMT_GBRAP12LE = 165,
            AV_PIX_FMT_GBRAP10BE = 166,
            AV_PIX_FMT_GBRAP10LE = 167,
            AV_PIX_FMT_MEDIACODEC = 168,
            AV_PIX_FMT_GRAY12BE = 169,
            AV_PIX_FMT_GRAY12LE = 170,
            AV_PIX_FMT_GRAY10BE = 171,
            AV_PIX_FMT_GRAY10LE = 172,
            AV_PIX_FMT_P016LE = 173,
            AV_PIX_FMT_P016BE = 174,
            AV_PIX_FMT_D3D11 = 175,
            AV_PIX_FMT_GRAY9BE = 176,
            AV_PIX_FMT_GRAY9LE = 177,
            AV_PIX_FMT_GBRPF32BE = 178,
            AV_PIX_FMT_GBRPF32LE = 179,
            AV_PIX_FMT_GBRAPF32BE = 180,
            AV_PIX_FMT_GBRAPF32LE = 181,
            AV_PIX_FMT_DRM_PRIME = 182,
            AV_PIX_FMT_OPENCL = 183,
            AV_PIX_FMT_GRAY14BE = 184,
            AV_PIX_FMT_GRAY14LE = 185,
            AV_PIX_FMT_GRAYF32BE = 186,
            AV_PIX_FMT_GRAYF32LE = 187,
            AV_PIX_FMT_YUVA422P12BE = 188,
            AV_PIX_FMT_YUVA422P12LE = 189,
            AV_PIX_FMT_YUVA444P12BE = 190,
            AV_PIX_FMT_YUVA444P12LE = 191,
            AV_PIX_FMT_NV24 = 192,
            AV_PIX_FMT_NV42 = 193,
            AV_PIX_FMT_VULKAN = 194,
            AV_PIX_FMT_Y210BE = 195,
            AV_PIX_FMT_Y210LE = 196,
            AV_PIX_FMT_X2RGB10LE = 197,
            AV_PIX_FMT_X2RGB10BE = 198,
            AV_PIX_FMT_X2BGR10LE = 199,
            AV_PIX_FMT_X2BGR10BE = 200,
            AV_PIX_FMT_P210BE = 201,
            AV_PIX_FMT_P210LE = 202,
            AV_PIX_FMT_P410BE = 203,
            AV_PIX_FMT_P410LE = 204,
            AV_PIX_FMT_P216BE = 205,
            AV_PIX_FMT_P216LE = 206,
            AV_PIX_FMT_P416BE = 207,
            AV_PIX_FMT_P416LE = 208,
            AV_PIX_FMT_VUYA = 209,
            AV_PIX_FMT_RGBAF16BE = 210,
            AV_PIX_FMT_RGBAF16LE = 211,
            AV_PIX_FMT_VUYX = 212,
            AV_PIX_FMT_P212BE = 213,
            AV_PIX_FMT_P212LE = 214,
            AV_PIX_FMT_P412BE = 215,
            AV_PIX_FMT_P412LE = 216,
            AV_PIX_FMT_RGBF32BE = 217,
            AV_PIX_FMT_RGBF32LE = 218,
            AV_PIX_FMT_RGBAF32BE = 219,
            AV_PIX_FMT_RGBAF32LE = 220,
            AV_PIX_FMT_YUV420P13BE = 221,
            AV_PIX_FMT_YUV420P13LE = 222,
            AV_PIX_FMT_YUV422P13BE = 223,
            AV_PIX_FMT_YUV422P13LE = 224,
            AV_PIX_FMT_YUV444P13BE = 225,
            AV_PIX_FMT_YUV444P13LE = 226,
            AV_PIX_FMT_GBRP13BE = 227,
            AV_PIX_FMT_GBRP13LE = 228,
            AV_PIX_FMT_GBRAP13BE = 229,
            AV_PIX_FMT_GBRAP13LE = 230,
            AV_PIX_FMT_NB = 231,
        }

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVFrame* av_frame_alloc();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_frame_unref(AVFrame* frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_free(AVFrame* frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_log_set_level(AVLog level);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_log_set_callback(av_log_set_callback_callback callback);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVLog av_log_get_level();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_log_format_line(void* ptr, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string fmt, byte* vl, byte* line, int lineSize, int* printPrefix);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodec* avcodec_find_decoder(AVCodecID id);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, void** options);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_close(AVCodecContext* avctx);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void avcodec_free_context(AVCodecContext** avctx);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVPacket* av_packet_alloc();

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void av_packet_unref(AVPacket* pkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void av_packet_free(AVPacket** pkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_version();

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodecHWConfig* avcodec_get_hw_config(AVCodec* codec, int index);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwdevice_ctx_create(AVBufferRef** device_ctx, AVHWDeviceType type, [MarshalAs(UnmanagedType.LPUTF8Str)] string device, void* opts, int flags);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVHWDeviceType av_hwdevice_iterate_types(AVHWDeviceType prev);

        [DllImport(AvUtilLibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern IntPtr av_hwdevice_get_type_name(AVHWDeviceType type);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwframe_transfer_data(AVFrame* dst, AVFrame* src, int flags);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_buffer_unref(AVBufferRef** buf);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVBufferRef* av_buffer_ref(AVBufferRef* buf);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVBufferRef* av_hwframe_ctx_alloc(AVBufferRef* device_ref);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwframe_ctx_init(AVBufferRef* ref_);
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AVBufferRef
    {
        public AVBuffer* buffer;
        public byte* data;
        public int size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AVCodecHWConfig
    {
        public FFmpegApi.AVHWDeviceType device_type;
        public int methods;
        public FFmpegApi.AVPixelFormat pix_fmt;
    }

    // 错误码常量
    internal static class FFmpegErrors
    {
        public const int AVERROR_EOF = -541478725;
        public const int AVERROR_EAGAIN = -11;
    }
}
