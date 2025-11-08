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
    }

    // 像素格式枚举定义
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
        
        // 硬件加速格式
        AV_PIX_FMT_VDPAU = 50,
        AV_PIX_FMT_VAAPI = 52,
        AV_PIX_FMT_DXVA2_VLD = 53,
        AV_PIX_FMT_VIDEOTOOLBOX = 157,
        AV_PIX_FMT_MEDIACODEC = 165,
        AV_PIX_FMT_CUDA = 166,
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

    // 移除重复的 AVCodecContext 定义，使用 AVCodecContext.cs 中的定义
}
