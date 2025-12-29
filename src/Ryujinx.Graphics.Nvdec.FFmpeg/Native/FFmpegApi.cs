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
            else if (OperatingSystem.IsAndroid())
            {
                return $"lib{libraryName}.so.{version}";
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
            AV_HWDEVICE_TYPE_VULKAN,
            AV_HWDEVICE_TYPE_D3D12VA,
        }
        
        internal enum AVPixelFormat
        {
            AV_PIX_FMT_NONE = -1,
            AV_PIX_FMT_YUV420P = 0,
            AV_PIX_FMT_YUVA420P = 1,
            AV_PIX_FMT_NV12 = 23,
            AV_PIX_FMT_NV21 = 24,
            AV_PIX_FMT_YUV420P10LE = 62,
            AV_PIX_FMT_YUV420P12LE = 77,
            AV_PIX_FMT_VAAPI = 77,
            AV_PIX_FMT_CUDA = 78,
            AV_PIX_FMT_D3D11 = 79,
            AV_PIX_FMT_DXVA2_VLD = 80,
            AV_PIX_FMT_VDPAU = 81,
            AV_PIX_FMT_VIDEOTOOLBOX = 82,
            AV_PIX_FMT_MEDIACODEC = 165,
            AV_PIX_FMT_VULKAN = 166,
        }

        internal static class AVERROR
        {
            public const int EAGAIN = -11;
            public const int EOF = -541478725;
            public const int EINVAL = -22;
            public const int INVALIDDATA = -1094995529;
        }

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVFrame* av_frame_alloc();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_frame_free(AVFrame** frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_frame_unref(AVFrame* frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_free(AVFrame* frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_frame_get_buffer(AVFrame* frame, int align);

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

        [LibraryImport(AvCodecLibraryName, EntryPoint = "avcodec_find_decoder_by_name")]
        internal static unsafe partial AVCodec* avcodec_find_decoder_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

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
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVBufferRef* av_hwdevice_ctx_alloc(AVHWDeviceType type);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwdevice_ctx_create(AVBufferRef** device_ctx, AVHWDeviceType type, [MarshalAs(UnmanagedType.LPUTF8Str)] string device, void* opts, int flags);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwdevice_ctx_init(AVBufferRef* @ref);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_buffer_unref(AVBufferRef** @ref);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVBufferRef* av_buffer_ref(AVBufferRef* @ref);
        
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodecHWConfig* avcodec_get_hw_config(AVCodec* codec, int index);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_hwframe_transfer_data(AVFrame* dst, AVFrame* src, int flags);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVHWDeviceType av_hwdevice_iterate_types(AVHWDeviceType prev);
        
        [LibraryImport(AvUtilLibraryName)]
        [return: MarshalAs(UnmanagedType.LPUTF8Str)]
        internal static unsafe partial string av_hwdevice_get_type_name(AVHWDeviceType type);
        
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);
        
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);
        
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_opt_set(void* obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string val, int search_flags);
        
        internal const int AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX = 0x0001;
    }
}
