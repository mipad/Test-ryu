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

        // ============ 常量定义 ============
        internal const int FF_THREAD_FRAME = 0x0001;
        
        // FFmpeg错误码
        internal const int AVERROR_EOF = -541478725; // MKTAG('E','O','F',' ')
        internal const int AVERROR_EAGAIN = -11;
        
        // 编解码器标志
        internal const int AV_CODEC_FLAG_LOW_DELAY = 0x00001000;
        internal const int AV_CODEC_FLAG_GLOBAL_HEADER = 0x00400000;
        
        // 编解码器标志2
        internal const int AV_CODEC_FLAG2_FAST = 0x00000001;
        internal const int AV_CODEC_FLAG2_NO_OUTPUT = 0x00000004;
        internal const int AV_CODEC_FLAG2_LOCAL_HEADER = 0x00000008;
        internal const int AV_CODEC_FLAG2_DROP_FRAME_TIMECODE = 0x00002000;
        internal const int AV_CODEC_FLAG2_CHUNKS = 0x00008000;
        internal const int AV_CODEC_FLAG2_IGNORE_CROP = 0x00010000;
        internal const int AV_CODEC_FLAG2_SHOW_ALL = 0x00400000;
        internal const int AV_CODEC_FLAG2_EXPORT_MVS = 0x10000000;
        internal const int AV_CODEC_FLAG2_SKIP_MANUAL = 0x20000000;
        
        // 帧类型
        internal const int AV_PICTURE_TYPE_NONE = 0;
        internal const int AV_PICTURE_TYPE_I = 1;
        internal const int AV_PICTURE_TYPE_P = 2;
        internal const int AV_PICTURE_TYPE_B = 3;
        
        // 像素格式
        internal const int AV_PIX_FMT_NONE = -1;
        internal const int AV_PIX_FMT_YUV420P = 0;
        internal const int AV_PIX_FMT_NV12 = 23;
        internal const int AV_PIX_FMT_YUVJ420P = 12;
        
        // 解码器选项
        internal const int AV_OPT_SEARCH_CHILDREN = 0x0001;
        
        // 委托
        public unsafe delegate void av_log_set_callback_callback(void* a0, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string a2, byte* a3);

        // avutil 函数
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVFrame* av_frame_alloc();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_frame_unref(AVFrame* frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_free(void* ptr);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_log_set_level(AVLog level);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_log_set_callback(av_log_set_callback_callback callback);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVLog av_log_get_level();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_log_format_line(void* ptr, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string fmt, byte* vl, byte* line, int lineSize, int* printPrefix);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_opt_set(void* obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string val, int search_flags);

        // 帧引用和克隆
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_frame_ref(AVFrame* dst, AVFrame* src);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVFrame* av_frame_clone(AVFrame* src);

        // avcodec 函数
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

        // 新的现代API
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);
    }
}
