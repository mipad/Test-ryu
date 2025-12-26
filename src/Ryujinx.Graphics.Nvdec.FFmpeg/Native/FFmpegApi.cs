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

        #region 硬件解码类型定义（公开访问）

        // 硬件设备类型枚举（公开）
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

        // 像素格式枚举（公开）
        public enum AVPixelFormat
        {
            AV_PIX_FMT_NONE = -1,
            AV_PIX_FMT_YUV420P = 0,
            AV_PIX_FMT_NV12 = 23,
            AV_PIX_FMT_MEDIACODEC = 168,
            // 其他格式根据需要添加
        }

        #endregion

        #region AVUtil 函数

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

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwdevice_ctx_create(AVBufferRef** device_ctx, AVHWDeviceType type, [MarshalAs(UnmanagedType.LPUTF8Str)] string device, void* opts, int flags);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVHWDeviceType av_hwdevice_iterate_types(AVHWDeviceType prev);

        // 使用传统的 DllImport 来避免 MarshalAs 问题
        [DllImport(AvUtilLibraryName, CallingConvention = CallingConvention.Cdecl)]
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

        #endregion

        #region AVCodec 函数

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

        #endregion
    }

    // AVBufferRef 结构（公开）
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AVBufferRef
    {
        public void* buffer;  // 改为 void* 避免 AVBuffer 类型问题
        public byte* data;
        public int size;
    }

    // AVCodecHWConfig 结构（公开）
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AVCodecHWConfig
    {
        public FFmpegApi.AVHWDeviceType device_type;
        public int methods;
        public FFmpegApi.AVPixelFormat pix_fmt;
    }

    // 错误码常量（内部）
    internal static class FFmpegErrors
    {
        public const int AVERROR_EOF = -541478725;
        public const int AVERROR_EAGAIN = -11;
    }
}
