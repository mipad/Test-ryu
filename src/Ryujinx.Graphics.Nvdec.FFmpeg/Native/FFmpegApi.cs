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

        // FFmpeg 错误码常量
        internal const int EAGAIN = -11;  // 资源暂时不可用，需要重试
        internal const int EOF = -541478725; // 文件结束

        // 硬件配置方法标志
        internal const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x01;
        internal const int AV_CODEC_HW_CONFIG_METHOD_HW_FRAMES_CTX = 0x02;
        internal const int AV_CODEC_HW_CONFIG_METHOD_INTERNAL = 0x04;
        internal const int AV_CODEC_HW_CONFIG_METHOD_AD_HOC = 0x08;

        private static readonly Dictionary<string, (int, int)> _librariesWhitelist = new()
        {
            { AvCodecLibraryName, (59, 61) },  // 扩展版本范围到 61
            { AvUtilLibraryName, (57, 59) },   // 扩展版本范围到 59
        };

        private static string FormatLibraryNameForCurrentOs(string libraryName, int version)
        {
            if (OperatingSystem.IsWindows())
            {
                return $"{libraryName}-{version}.dll";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"lib{libraryName}.so.{version}";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return $"lib{libraryName}.{version}.dylib";
            }
            else
            {
                throw new NotImplementedException($"Unsupported OS for FFmpeg: {RuntimeInformation.RuntimeIdentifier}");
            }
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
        internal static unsafe partial AVCodec* avcodec_find_decoder_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, void** options);

        // 注释掉已弃用的 avcodec_close，使用 avcodec_free_context 替代
        // [LibraryImport(AvCodecLibraryName)]
        // internal static unsafe partial int avcodec_close(AVCodecContext* avctx);

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

        // 添加新 API 支持
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void avcodec_flush_buffers(AVCodecContext* avctx);

        // 硬件解码支持
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVHWDeviceType av_hwdevice_find_type_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVBufferRef* av_hwdevice_ctx_alloc(AVHWDeviceType type);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int av_hwdevice_ctx_create(AVBufferRef** device_ctx, AVHWDeviceType type, [MarshalAs(UnmanagedType.LPUTF8Str)] string device, void* opts, int flags);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVBufferRef* av_buffer_ref(AVBufferRef* buf);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_buffer_unref(AVBufferRef** buf);

        // 添加硬件配置查询 API
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodecHWConfig* avcodec_get_hw_config(AVCodec* codec, int index);

        // 新增：硬件帧转换API
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwframe_transfer_data(AVFrame* dst, AVFrame* src, int flags);

        // 新增：帧引用API
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_frame_ref(AVFrame* dst, AVFrame* src);
    }

    // 像素格式枚举定义 - 扩展版本
    internal enum AVPixelFormat
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
        AV_PIX_FMT_XVMC_MPEG2_MC = 15,
        AV_PIX_FMT_XVMC_MPEG2_IDCT = 16,
        AV_PIX_FMT_UYVY422 = 17,
        AV_PIX_FMT_UYYVYY411 = 18,
        AV_PIX_FMT_BGR8 = 19,
        AV_PIX_FMT_BGR4 = 20,
        AV_PIX_FMT_BGR4_BYTE = 21,
        AV_PIX_FMT_RGB8 = 22,
        AV_PIX_FMT_RGB4 = 23,
        AV_PIX_FMT_RGB4_BYTE = 24,
        AV_PIX_FMT_NV12 = 25,
        AV_PIX_FMT_NV21 = 26,
        
        // 扩展更多像素格式
        AV_PIX_FMT_ARGB = 27,
        AV_PIX_FMT_RGBA = 28,
        AV_PIX_FMT_ABGR = 29,
        AV_PIX_FMT_BGRA = 30,
        AV_PIX_FMT_GRAY16BE = 31,
        AV_PIX_FMT_GRAY16LE = 32,
        AV_PIX_FMT_YUV440P = 33,
        AV_PIX_FMT_YUVJ440P = 34,
        AV_PIX_FMT_YUVA420P = 35,
        AV_PIX_FMT_RGB48BE = 36,
        AV_PIX_FMT_RGB48LE = 37,
        AV_PIX_FMT_RGB565BE = 38,
        AV_PIX_FMT_RGB565LE = 39,
        AV_PIX_FMT_RGB555BE = 40,
        AV_PIX_FMT_RGB555LE = 41,
        AV_PIX_FMT_BGR565BE = 42,
        AV_PIX_FMT_BGR565LE = 43,
        AV_PIX_FMT_BGR555BE = 44,
        AV_PIX_FMT_BGR555LE = 45,
        
        // 10位和12位格式
        AV_PIX_FMT_YUV420P9BE = 46,
        AV_PIX_FMT_YUV420P9LE = 47,
        AV_PIX_FMT_YUV420P10BE = 48,
        AV_PIX_FMT_YUV420P10LE = 49,
        AV_PIX_FMT_YUV422P10BE = 50,
        AV_PIX_FMT_YUV422P10LE = 51,
        AV_PIX_FMT_YUV444P9BE = 52,
        AV_PIX_FMT_YUV444P9LE = 53,
        AV_PIX_FMT_YUV444P10BE = 54,
        AV_PIX_FMT_YUV444P10LE = 55,
        AV_PIX_FMT_YUV420P12BE = 56,
        AV_PIX_FMT_YUV420P12LE = 57,
        AV_PIX_FMT_YUV422P12BE = 58,
        AV_PIX_FMT_YUV422P12LE = 59,
        AV_PIX_FMT_YUV444P12BE = 60,
        AV_PIX_FMT_YUV444P12LE = 61,
        
        // 硬件加速格式
        AV_PIX_FMT_VDPAU = 50,
        AV_PIX_FMT_VAAPI = 52,
        AV_PIX_FMT_DXVA2_VLD = 53,
        AV_PIX_FMT_VIDEOTOOLBOX = 157,
        AV_PIX_FMT_MEDIACODEC = 165,
        AV_PIX_FMT_CUDA = 166,
        
        // 更多硬件格式
        AV_PIX_FMT_D3D11 = 170,
        AV_PIX_FMT_OPENCL = 171,
    }

    // 硬件解码相关类型定义
    internal enum AVHWDeviceType
    {
        AV_HWDEVICE_TYPE_NONE,
        AV_HWDEVICE_TYPE_VDPAU,
        AV_HWDEVICE_TYPE_CUDA,
        AV_HWDEVICE_TYPE_VAAPI,
        AV_HWDEVICE_TYPE_DXVA2,
        AV_HWDEVICE_TYPE_QSV,
        AV_HWDEVICE_TYPE_VIDEOTOOLBOX,
        AV_HWDEVICE_TYPE_D3D11VA,
        AV_HWDEVICE_TYPE_DRM,
        AV_HWDEVICE_TYPE_OPENCL,
        AV_HWDEVICE_TYPE_MEDIACODEC,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVBufferRef
    {
        public AVBuffer* Buffer;
        public byte* Data;
        public int Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVBuffer
    {
        public byte* Data;
        public int Size;
        public int RefCount;
        public void* Free;
        public void* Opaque;
        public void* FreeCallback;
    }

    // AVCodecHWConfig 结构定义
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AVCodecHWConfig
    {
        public AVHWDeviceType device_type;
        public AVPixelFormat pix_fmt;
        public int methods;
        public int device_caps;
    }
}