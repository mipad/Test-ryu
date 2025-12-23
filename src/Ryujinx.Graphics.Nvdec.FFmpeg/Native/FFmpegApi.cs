// FFmpegApi.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    static partial class FFmpegApi
    {
        // 直接指向我们的主库
        private const string RyujinxLibraryName = "libryujinxjni";
        
        // 修改白名单，直接加载我们的主库
        private static readonly Dictionary<string, (int, int)> _librariesWhitelist = new()
        {
            { "avcodec", (0, 0) },  // 版本号不重要，我们直接重定向
            { "avutil", (0, 0) },
            { "swresample", (0, 0) },
            { "swscale", (0, 0) }
        };

        private static bool TryLoadWhitelistedLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath, out IntPtr handle)
        {
            handle = IntPtr.Zero;

            // 对于 FFmpeg 库，直接加载我们的主库
            if (_librariesWhitelist.ContainsKey(libraryName))
            {
                // Android 上加载 libryujinxjni.so
                if (NativeLibrary.TryLoad("libryujinxjni.so", assembly, searchPath, out handle))
                {
                    return true;
                }
                
                // 如果失败，尝试其他可能的名称
                if (NativeLibrary.TryLoad("ryujinxjni", assembly, searchPath, out handle))
                {
                    return true;
                }
            }

            return false;
        }

        static FFmpegApi()
        {
            NativeLibrary.SetDllImportResolver(typeof(FFmpegApi).Assembly, (name, assembly, path) =>
            {
                // 重定向所有 FFmpeg 库到我们的主库
                if (name == "avutil" || name == "avcodec" || name == "swresample" || name == "swscale")
                {
                    if (TryLoadWhitelistedLibrary(name, assembly, path, out nint handle))
                    {
                        return handle;
                    }
                }

                return IntPtr.Zero;
            });
        }

        public unsafe delegate void av_log_set_callback_callback(void* a0, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string a2, byte* a3);

        // 直接从 libryujinxjni.so 导入函数
        [DllImport(RyujinxLibraryName, EntryPoint = "av_frame_alloc")]
        internal static unsafe extern AVFrame* av_frame_alloc();

        [DllImport(RyujinxLibraryName, EntryPoint = "av_frame_unref")]
        internal static unsafe extern void av_frame_unref(AVFrame* frame);

        [DllImport(RyujinxLibraryName, EntryPoint = "av_free")]
        internal static unsafe extern void av_free(AVFrame* frame);

        [DllImport(RyujinxLibraryName, EntryPoint = "av_log_set_level")]
        internal static unsafe extern void av_log_set_level(AVLog level);

        [DllImport(RyujinxLibraryName, EntryPoint = "av_log_set_callback")]
        internal static unsafe extern void av_log_set_callback(av_log_set_callback_callback callback);

        [DllImport(RyujinxLibraryName, EntryPoint = "av_log_get_level")]
        internal static unsafe extern AVLog av_log_get_level();

        [DllImport(RyujinxLibraryName, EntryPoint = "av_log_format_line")]
        internal static unsafe extern void av_log_format_line(void* ptr, AVLog level, [MarshalAs(UnmanagedType.LPUTF8Str)] string fmt, byte* vl, byte* line, int lineSize, int* printPrefix);

        [DllImport(RyujinxLibraryName, EntryPoint = "avcodec_find_decoder")]
        internal static unsafe extern AVCodec* avcodec_find_decoder(AVCodecID id);

        [DllImport(RyujinxLibraryName, EntryPoint = "avcodec_alloc_context3")]
        internal static unsafe extern AVCodecContext* avcodec_alloc_context3(AVCodec* codec);

        [DllImport(RyujinxLibraryName, EntryPoint = "avcodec_open2")]
        internal static unsafe extern int avcodec_open2(AVCodecContext* avctx, AVCodec* codec, void** options);

        [DllImport(RyujinxLibraryName, EntryPoint = "avcodec_close")]
        internal static unsafe extern int avcodec_close(AVCodecContext* avctx);

        [DllImport(RyujinxLibraryName, EntryPoint = "avcodec_free_context")]
        internal static unsafe extern void avcodec_free_context(AVCodecContext** avctx);

        [DllImport(RyujinxLibraryName, EntryPoint = "av_packet_alloc")]
        internal static unsafe extern AVPacket* av_packet_alloc();

        [DllImport(RyujinxLibraryName, EntryPoint = "av_packet_unref")]
        internal static unsafe extern void av_packet_unref(AVPacket* pkt);

        [DllImport(RyujinxLibraryName, EntryPoint = "av_packet_free")]
        internal static unsafe extern void av_packet_free(AVPacket** pkt);

        [DllImport(RyujinxLibraryName, EntryPoint = "avcodec_version")]
        internal static unsafe extern int avcodec_version();

        // swresample 函数
        [DllImport(RyujinxLibraryName, EntryPoint = "swr_alloc")]
        internal static unsafe extern SwrContext* swr_alloc();

        [DllImport(RyujinxLibraryName, EntryPoint = "swr_init")]
        internal static unsafe extern int swr_init(SwrContext* s);

        [DllImport(RyujinxLibraryName, EntryPoint = "swr_convert")]
        internal static unsafe extern int swr_convert(SwrContext* s, byte** outData, int outCount, byte** inData, int inCount);

        [DllImport(RyujinxLibraryName, EntryPoint = "swr_free")]
        internal static unsafe extern void swr_free(SwrContext** s);

        // swscale 函数
        [DllImport(RyujinxLibraryName, EntryPoint = "sws_getContext")]
        internal static unsafe extern SwsContext* sws_getContext(int srcW, int srcH, AVPixelFormat srcFormat,
                                                                int dstW, int dstH, AVPixelFormat dstFormat,
                                                                int flags, SwsFilter* srcFilter,
                                                                SwsFilter* dstFilter, double* param);

        [DllImport(RyujinxLibraryName, EntryPoint = "sws_scale")]
        internal static unsafe extern int sws_scale(SwsContext* c, byte*[] srcSlice, int[] srcStride,
                                                   int srcSliceY, int srcSliceH,
                                                   byte*[] dst, int[] dstStride);

        [DllImport(RyujinxLibraryName, EntryPoint = "sws_freeContext")]
        internal static unsafe extern void sws_freeContext(SwsContext* swsContext);
    }
}
