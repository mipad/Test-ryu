// FFmpegApi (1).cs
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    static partial class FFmpegApi
    {
        // 修改：直接指向主库
        public const string RyujinxLibraryName = "ryujinxjni";

        private static readonly Dictionary<string, (int, int)> _librariesWhitelist = new()
        {
            { RyujinxLibraryName, (1, 1) }, // 只需要主库版本
        };

        private static string FormatLibraryNameForCurrentOs(string libraryName, int version)
        {
            if (OperatingSystem.IsWindows())
            {
                return $"{libraryName}.dll";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"lib{libraryName}.so";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return $"lib{libraryName}.dylib";
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
            // 静态链接 FFmpeg，所以不需要动态加载
            // 保留此代码只是为了兼容性
        }

        public unsafe delegate void av_log_set_callback_callback(void* a0, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string a2, byte* a3);

        // 修改所有 DllImport 指向主库
        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_frame_alloc")]
        internal static unsafe partial AVFrame* av_frame_alloc();

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_frame_unref")]
        internal static unsafe partial void av_frame_unref(AVFrame* frame);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_free")]
        internal static unsafe partial void av_free(AVFrame* frame);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_log_set_level")]
        internal static unsafe partial void av_log_set_level(AVLog level);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_log_set_callback")]
        internal static unsafe partial void av_log_set_callback(av_log_set_callback_callback callback);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_log_get_level")]
        internal static unsafe partial AVLog av_log_get_level();

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_log_format_line")]
        internal static unsafe partial void av_log_format_line(void* ptr, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string fmt, byte* vl, byte* line, int lineSize, int* printPrefix);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "avcodec_find_decoder")]
        internal static unsafe partial AVCodec* avcodec_find_decoder(AVCodecID id);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "avcodec_alloc_context3")]
        internal static unsafe partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "avcodec_open2")]
        internal static unsafe partial int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, void** options);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "avcodec_close")]
        internal static unsafe partial int avcodec_close(AVCodecContext* avctx);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "avcodec_free_context")]
        internal static unsafe partial void avcodec_free_context(AVCodecContext** avctx);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_packet_alloc")]
        internal static unsafe partial AVPacket* av_packet_alloc();

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_packet_unref")]
        internal static unsafe partial void av_packet_unref(AVPacket* pkt);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "av_packet_free")]
        internal static unsafe partial void av_packet_free(AVPacket** pkt);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "avcodec_version")]
        internal static unsafe partial int avcodec_version();

        // 添加 swresample 和 swscale 的函数
        [LibraryImport(RyujinxLibraryName, EntryPoint = "swr_alloc")]
        internal static unsafe partial SwrContext* swr_alloc();

        [LibraryImport(RyujinxLibraryName, EntryPoint = "swr_init")]
        internal static unsafe partial int swr_init(SwrContext* s);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "swr_free")]
        internal static unsafe partial void swr_free(SwrContext** s);

        [LibraryImport(RyujinxLibraryName, EntryPoint = "sws_getContext")]
        internal static unsafe partial SwsContext* sws_getContext(int srcW, int srcH, AVPixelFormat srcFormat,
                                                                  int dstW, int dstH, AVPixelFormat dstFormat,
                                                                  int flags, SwsFilter* srcFilter,
                                                                  SwsFilter* dstFilter, double* param);
    }
}
