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
        public const string AvFilterLibraryName = "avfilter";
        public const string SwScaleLibraryName = "swscale";

        private static readonly Dictionary<string, (int, int)> _librariesWhitelist = new()
        {
            { AvCodecLibraryName, (59, 61) },
            { AvUtilLibraryName, (57, 59) },
            { AvFilterLibraryName, (7, 9) },
            { SwScaleLibraryName, (5, 7) },
        };

        // 硬件设备类型枚举
        public enum AVHWDeviceType
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
            AV_HWDEVICE_TYPE_MEDIACODEC,  // Android 硬件解码
            AV_HWDEVICE_TYPE_VULKAN,
            AV_HWDEVICE_TYPE_D3D12VA,
        }

        // 像素格式
        public enum AVPixelFormat
        {
            AV_PIX_FMT_NONE = -1,
            AV_PIX_FMT_YUV420P = 0,
            AV_PIX_FMT_NV12 = 23,
            AV_PIX_FMT_YUV420P10LE = 62,
            AV_PIX_FMT_MEDIACODEC = 182,  // Android MediaCodec 表面
        }

        // 硬件帧上下文
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct AVBufferRef
        {
            public AVBuffer* buffer;
            public byte* data;
            public int size;
        }

        // 硬件帧参数
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct AVHWFramesContext
        {
            public void* av_class;
            public AVBufferRef* device_ref;
            public AVHWDeviceType device_type;
            public AVPixelFormat format;
            public int width;
            public int height;
            public AVRational sample_aspect_ratio;
            public void* pool;
            public int initial_pool_size;
        }

        // 硬件设备上下文
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct AVHWDeviceContext
        {
            public void* av_class;
            public AVHWDeviceType type;
            public void* hwctx;
            public void* free;
            public void* user_opaque;
        }

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
                else if (name == AvFilterLibraryName && TryLoadWhitelistedLibrary(AvFilterLibraryName, assembly, path, out handle))
                {
                    return handle;
                }
                else if (name == SwScaleLibraryName && TryLoadWhitelistedLibrary(SwScaleLibraryName, assembly, path, out handle))
                {
                    return handle;
                }

                return IntPtr.Zero;
            });
        }

        public unsafe delegate void av_log_set_callback_callback(void* a0, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string a2, byte* a3);

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
        internal static unsafe partial AVBufferRef* av_hwframe_ctx_alloc(AVBufferRef* device_ref);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwframe_ctx_init(AVBufferRef* ref_);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwdevice_ctx_create(AVBufferRef** device_ctx, AVHWDeviceType type, [MarshalAs(UnmanagedType.LPUTF8Str)] string device, void* opts, int flags);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVHWDeviceType av_hwdevice_iterate_types(AVHWDeviceType prev);

        [LibraryImport(AvUtilLibraryName)]
        [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(ConstCharPtrMarshaler))]
        internal static unsafe partial string av_hwdevice_get_type_name(AVHWDeviceType type);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwframe_transfer_data(AVFrame* dst, AVFrame* src, int flags);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_buffer_unref(AVBufferRef** buf);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVBufferRef* av_buffer_ref(AVBufferRef* buf);

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

        #region SwScale 函数

        [LibraryImport(SwScaleLibraryName)]
        internal static unsafe partial SwsContext* sws_getContext(
            int srcW, int srcH, AVPixelFormat srcFormat,
            int dstW, int dstH, AVPixelFormat dstFormat,
            int flags, SwsFilter* srcFilter, SwsFilter* dstFilter, double* param);

        [LibraryImport(SwScaleLibraryName)]
        internal static unsafe partial int sws_scale(SwsContext* c, byte*[] srcSlice, int[] srcStride,
            int srcSliceY, int srcSliceH, byte*[] dst, int[] dstStride);

        [LibraryImport(SwScaleLibraryName)]
        internal static unsafe partial void sws_freeContext(SwsContext* swsContext);

        #endregion
    }

    // 用于字符串返回类型的 Marshaler
    public class ConstCharPtrMarshaler : ICustomMarshaler
    {
        private static readonly ConstCharPtrMarshaler Instance = new();

        public static ICustomMarshaler GetInstance(string cookie) => Instance;

        public void CleanUpManagedData(object ManagedObj)
        {
        }

        public void CleanUpNativeData(IntPtr pNativeData)
        {
        }

        public int GetNativeDataSize() => IntPtr.Size;

        public IntPtr MarshalManagedToNative(object ManagedObj) => IntPtr.Zero;

        public object MarshalNativeToManaged(IntPtr pNativeData)
        {
            if (pNativeData == IntPtr.Zero)
                return null;

            return Marshal.PtrToStringAnsi(pNativeData);
        }
    }

    // 硬件配置结构
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AVCodecHWConfig
    {
        public AVHWDeviceType device_type;
        public int methods;
        public FFmpegApi.AVPixelFormat pix_fmt;
    }

    // SwsContext 结构
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SwsContext
    {
        private readonly IntPtr opaque;
    }

    // SwsFilter 结构
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SwsFilter
    {
        // 占位符，实际结构在 libswscale 内部
    }
}
