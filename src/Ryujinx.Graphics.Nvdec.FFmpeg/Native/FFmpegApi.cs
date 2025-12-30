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
            { AvCodecLibraryName, (58, 62) }, // 更新版本范围到6.x
            { AvUtilLibraryName, (56, 60) },  // 更新版本范围到6.x
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

        // avutil 函数
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial AVFrame* av_frame_alloc();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_frame_unref(AVFrame* frame);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_frame_ref(AVFrame* dst, AVFrame* src);

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_frame_free(AVFrame** frame);

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

        // avcodec 函数 - 编解码器管理
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodec* avcodec_find_decoder(AVCodecID id);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodec* avcodec_find_decoder_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, void** options);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_close(AVCodecContext* avctx);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void avcodec_free_context(AVCodecContext** avctx);

        // 包管理
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial AVPacket* av_packet_alloc();

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void av_packet_unref(AVPacket* pkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void av_packet_free(AVPacket** pkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int av_new_packet(AVPacket* pkt, int size);

        // 新API (推荐) - 从FFmpeg 3.1开始
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_send_packet(AVCodecContext* avctx, AVPacket* avpkt);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_send_frame(AVCodecContext* avctx, AVFrame* frame);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_receive_packet(AVCodecContext* avctx, AVPacket* avpkt);

        // 版本信息
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_version();

        [LibraryImport(AvCodecLibraryName)]
        [return: MarshalAs(UnmanagedType.LPUTF8Str)]
        internal static unsafe partial string avcodec_configuration();

        // 参数设置
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_parameters_to_context(AVCodecContext* codec, AVCodecParameters* par);

        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial int avcodec_parameters_from_context(AVCodecParameters* par, AVCodecContext* codec);

        // 解码刷新
        [LibraryImport(AvCodecLibraryName)]
        internal static unsafe partial void avcodec_flush_buffers(AVCodecContext* avctx);

        // 性能相关
        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial long av_gettime();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial int av_get_cpu_flags();

        [LibraryImport(AvUtilLibraryName)]
        internal static unsafe partial void av_force_cpu_flags(int flags);
    }
}
