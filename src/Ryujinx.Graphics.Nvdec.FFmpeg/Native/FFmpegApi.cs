using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    static partial class FFmpegApi
    {
        public const string AvCodecLibraryName = "avcodec";
        public const string AvUtilLibraryName = "avutil";
        public const string SwScaleLibraryName = "swscale";
        public const string SwResampleLibraryName = "swresample";
        public const string HardwareDecoderLibraryName = "hardware_decoder";

        private static readonly Dictionary<string, (int, int)> _librariesWhitelist = new()
        {
            { AvCodecLibraryName, (59, 61) },
            { AvUtilLibraryName, (57, 59) },
            { SwScaleLibraryName, (6, 7) },
            { SwResampleLibraryName, (3, 4) },
        };

        // Android 平台使用不同的库名规则
#if ANDROID
        [SupportedOSPlatform("android")]
        private static string FormatLibraryNameForAndroid(string libraryName, int version)
        {
            // Android 上通常使用 lib[libraryName].so
            return $"lib{libraryName}.so";
        }
#endif

        private static string FormatLibraryNameForCurrentOs(string libraryName, int version)
        {
#if ANDROID
            return FormatLibraryNameForAndroid(libraryName, version);
#else
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
#endif
        }

        private static bool TryLoadWhitelistedLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath, out IntPtr handle)
        {
            handle = IntPtr.Zero;

            // 如果是Android平台，尝试加载硬件解码器库
#if ANDROID
            if (libraryName == HardwareDecoderLibraryName)
            {
                // Android 上硬件解码器库名为 libhardware_decoder.so
                string androidLibName = "libhardware_decoder.so";
                Console.WriteLine($"Attempting to load hardware decoder library: {androidLibName}");
                
                if (NativeLibrary.TryLoad(androidLibName, assembly, searchPath, out handle))
                {
                    Console.WriteLine($"Successfully loaded hardware decoder library");
                    return true;
                }
                else
                {
                    Console.WriteLine($"Failed to load hardware decoder library: {androidLibName}");
                    return false;
                }
            }
#endif

            if (_librariesWhitelist.TryGetValue(libraryName, out var value))
            {
                (int minVersion, int maxVersion) = value;

                for (int version = maxVersion; version >= minVersion; version--)
                {
                    string libName = FormatLibraryNameForCurrentOs(libraryName, version);
                    Console.WriteLine($"Attempting to load library: {libName}");
                    
                    if (NativeLibrary.TryLoad(libName, assembly, searchPath, out handle))
                    {
                        Console.WriteLine($"Successfully loaded library: {libName}");
                        return true;
                    }
                }
            }

            Console.WriteLine($"Failed to load library: {libraryName}");
            return false;
        }

        static FFmpegApi()
        {
            Console.WriteLine($"FFmpegApi static constructor called. Platform: {RuntimeInformation.RuntimeIdentifier}");
            
            NativeLibrary.SetDllImportResolver(typeof(FFmpegApi).Assembly, (name, assembly, path) =>
            {
                Console.WriteLine($"DllImportResolver called for library: {name}");

                if (name == AvUtilLibraryName && TryLoadWhitelistedLibrary(AvUtilLibraryName, assembly, path, out nint handle))
                {
                    return handle;
                }
                else if (name == AvCodecLibraryName && TryLoadWhitelistedLibrary(AvCodecLibraryName, assembly, path, out handle))
                {
                    return handle;
                }
                else if (name == SwScaleLibraryName && TryLoadWhitelistedLibrary(SwScaleLibraryName, assembly, path, out handle))
                {
                    return handle;
                }
                else if (name == SwResampleLibraryName && TryLoadWhitelistedLibrary(SwResampleLibraryName, assembly, path, out handle))
                {
                    return handle;
                }
#if ANDROID
                else if (name == HardwareDecoderLibraryName && TryLoadWhitelistedLibrary(HardwareDecoderLibraryName, assembly, path, out handle))
                {
                    return handle;
                }
#endif

                Console.WriteLine($"No library handler found for: {name}");
                return IntPtr.Zero;
            });
        }

        // 硬件解码器API函数 - 只在Android平台可用
#if ANDROID
        [SupportedOSPlatform("android")]
        public static class HardwareDecoderApi
        {
            [DllImport(HardwareDecoderLibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr hw_create(SimpleHWCodecType codec, int width, int height, [MarshalAs(UnmanagedType.I1)] bool use_hw);

            [DllImport(HardwareDecoderLibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern int hw_decode(IntPtr handle, byte[] data, int size, ref SimpleHWFrame frame);

            [DllImport(HardwareDecoderLibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void hw_destroy(IntPtr handle);

            [DllImport(HardwareDecoderLibraryName, CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.I1)]
            public static extern bool hw_is_available();

            [DllImport(HardwareDecoderLibraryName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr hw_get_last_error(IntPtr handle);
        }
#endif

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
    }
}
