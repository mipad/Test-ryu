// HardwareDecoder.cs
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Security;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Native
{
    // ===================== 枚举定义 =====================
    
    public enum HWCodecType : int
    {
        HW_CODEC_H264 = 0,
        HW_CODEC_VP8 = 1,
        HW_CODEC_VP9 = 2,
        HW_CODEC_HEVC = 3,
        HW_CODEC_AV1 = 4
    }
    
    public enum HWPixelFormat : int
    {
        HW_PIX_FMT_NONE = -1,
        HW_PIX_FMT_YUV420P = 0,
        HW_PIX_FMT_NV12 = 1,
        HW_PIX_FMT_NV21 = 2,
        HW_PIX_FMT_RGBA = 3,
        HW_PIX_FMT_BGRA = 4,
        HW_PIX_FMT_ARGB = 5,
        HW_PIX_FMT_ABGR = 6
    }
    
    public enum HWDecoderError : int
    {
        HW_DECODER_SUCCESS = 0,
        HW_DECODER_ERROR_INVALID_HANDLE = -1,
        HW_DECODER_ERROR_INVALID_PARAMETER = -2,
        HW_DECODER_ERROR_OUT_OF_MEMORY = -3,
        HW_DECODER_ERROR_INIT_FAILED = -4,
        HW_DECODER_ERROR_DECODE_FAILED = -5,
        HW_DECODER_ERROR_FLUSH_FAILED = -6,
        HW_DECODER_ERROR_CLOSE_FAILED = -7,
        HW_DECODER_ERROR_NOT_SUPPORTED = -8,
        HW_DECODER_ERROR_TIMEOUT = -9,
        HW_DECODER_ERROR_EOF = -10,
        HW_DECODER_ERROR_TRY_AGAIN = -11,
        HW_DECODER_ERROR_BUFFER_FULL = -12,
        HW_DECODER_ERROR_BUFFER_EMPTY = -13,
        HW_DECODER_ERROR_HARDWARE_CHANGED = -14,
        HW_DECODER_ERROR_SURFACE_CHANGED = -15,
        HW_DECODER_ERROR_FORMAT_CHANGED = -16,
        HW_DECODER_ERROR_STREAM_CHANGED = -17,
        HW_DECODER_ERROR_DISPLAY_CHANGED = -18,
        HW_DECODER_ERROR_RESOLUTION_CHANGED = -19,
        HW_DECODER_ERROR_BITRATE_CHANGED = -20,
        HW_DECODER_ERROR_FRAMERATE_CHANGED = -21,
        HW_DECODER_ERROR_CODEC_CHANGED = -22,
        HW_DECODER_ERROR_PROFILE_CHANGED = -23,
        HW_DECODER_ERROR_LEVEL_CHANGED = -24,
        HW_DECODER_ERROR_UNKNOWN = -100
    }
    
    public enum HWLogLevel : int
    {
        HW_LOG_QUIET = -8,
        HW_LOG_PANIC = 0,
        HW_LOG_FATAL = 8,
        HW_LOG_ERROR = 16,
        HW_LOG_WARNING = 24,
        HW_LOG_INFO = 32,
        HW_LOG_VERBOSE = 40,
        HW_LOG_DEBUG = 48,
        HW_LOG_TRACE = 56
    }
    
    // ===================== 结构体定义 =====================
    
    [StructLayout(LayoutKind.Sequential)]
    public struct HWDecoderConfig
    {
        public int width;                 // 视频宽度
        public int height;                // 视频高度
        public int bit_depth;             // 位深度 (8, 10, 12)
        public int chroma_format;         // 色度格式
        [MarshalAs(UnmanagedType.I1)]
        public bool low_latency;          // 低延迟模式
        public int thread_count;          // 线程数 (0=自动)
        public int max_ref_frames;        // 最大参考帧数
        [MarshalAs(UnmanagedType.I1)]
        public bool enable_deblocking;    // 启用去块滤波
        [MarshalAs(UnmanagedType.I1)]
        public bool enable_sao;           // 启用SAO滤波
        public int profile;               // 编码器档次
        public int level;                 // 编码器级别
        
        // 构造函数
        public HWDecoderConfig(int width, int height)
        {
            this.width = width;
            this.height = height;
            bit_depth = 8;
            chroma_format = 1; // 4:2:0
            low_latency = false;
            thread_count = 0;
            max_ref_frames = 16;
            enable_deblocking = true;
            enable_sao = true;
            profile = 100; // High profile
            level = 40; // Level 4.0
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct HWFrameData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public IntPtr[] data;           // 平面数据指针 [Y, U, V, A]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public int[] linesize;          // 每个平面的行大小
        public int width;               // 帧宽度
        public int height;              // 帧高度
        public HWPixelFormat format;    // 像素格式
        public long pts;                // 显示时间戳
        public long dts;                // 解码时间戳
        public long duration;           // 帧持续时间
        [MarshalAs(UnmanagedType.I1)]
        public bool key_frame;          // 是否为关键帧
        [MarshalAs(UnmanagedType.I1)]
        public bool interlaced;         // 是否为隔行扫描
        public int repeat_pict;         // 重复图像计数
        public int coded_picture_number;   // 编码图像序号
        public int display_picture_number; // 显示图像序号
        public int quality;                // 图像质量 (1-FF_LAMBDA_MAX)
        public long reordered_opaque;   // 重新排序不透明数据
        public int sample_aspect_ratio_num;  // 采样宽高比分子
        public int sample_aspect_ratio_den;  // 采样宽高比分母
        public int color_range;         // 颜色范围
        public int color_primaries;     // 颜色原色
        public int color_trc;           // 颜色传输特性
        public int colorspace;          // 颜色空间
        public int chroma_location;     // 色度位置
        public int best_effort_timestamp;  // 尽力而为时间戳
        public int pkt_pos;             // 包位置
        public int pkt_size;            // 包大小
        public int metadata_count;      // 元数据计数
        public IntPtr metadata;         // 元数据指针数组
        public int decode_error_flags;  // 解码错误标志
        public int channels;            // 音频通道数
        public int channel_layout;      // 音频通道布局
        public int nb_samples;          // 音频采样数
        public int sample_rate;         // 音频采样率
        public int audio_channels;      // 音频通道数 (已弃用，使用channels)
        public int audio_channel_layout; // 音频通道布局 (已弃用，使用channel_layout)
        public int audio_sample_rate;   // 音频采样率 (已弃用，使用sample_rate)
        public int audio_sample_format; // 音频采样格式
        public int audio_frame_size;    // 音频帧大小
        public int audio_buffer_size;   // 音频缓冲区大小
        
        // 构造函数
        public HWFrameData()
        {
            data = new IntPtr[4];
            linesize = new int[4];
            width = 0;
            height = 0;
            format = HWPixelFormat.HW_PIX_FMT_NONE;
            pts = 0;
            dts = 0;
            duration = 0;
            key_frame = false;
            interlaced = false;
            repeat_pict = 0;
            coded_picture_number = 0;
            display_picture_number = 0;
            quality = 0;
            reordered_opaque = 0;
            sample_aspect_ratio_num = 1;
            sample_aspect_ratio_den = 1;
            color_range = 0;
            color_primaries = 0;
            color_trc = 0;
            colorspace = 0;
            chroma_location = 0;
            best_effort_timestamp = 0;
            pkt_pos = 0;
            pkt_size = 0;
            metadata_count = 0;
            metadata = IntPtr.Zero;
            decode_error_flags = 0;
            channels = 0;
            channel_layout = 0;
            nb_samples = 0;
            sample_rate = 0;
            audio_channels = 0;
            audio_channel_layout = 0;
            audio_sample_rate = 0;
            audio_sample_format = 0;
            audio_frame_size = 0;
            audio_buffer_size = 0;
        }
        
        // 获取平面大小
        public int GetPlaneSize(int plane)
        {
            if (plane < 0 || plane >= 4)
                return 0;
            
            if (data[plane] == IntPtr.Zero || linesize[plane] <= 0)
                return 0;
            
            int planeHeight = height;
            if (plane > 0)
            {
                // 色度平面高度减半
                planeHeight = (planeHeight + 1) / 2;
            }
            
            return linesize[plane] * planeHeight;
        }
        
        // 获取帧总大小
        public int GetFrameSize()
        {
            int totalSize = 0;
            for (int i = 0; i < 4; i++)
            {
                totalSize += GetPlaneSize(i);
            }
            return totalSize;
        }
        
        // 检查帧是否有效
        public bool IsValid()
        {
            if (width <= 0 || height <= 0)
                return false;
            
            if (data[0] == IntPtr.Zero || linesize[0] <= 0)
                return false;
            
            return true;
        }
        
        // 复制平面数据到字节数组
        public byte[] GetPlaneData(int plane)
        {
            int size = GetPlaneSize(plane);
            if (size <= 0 || data[plane] == IntPtr.Zero)
                return null;
            
            byte[] buffer = new byte[size];
            Marshal.Copy(data[plane], buffer, 0, size);
            return buffer;
        }
        
        // 复制所有平面数据到字节数组列表
        public List<byte[]> GetAllPlaneData()
        {
            List<byte[]> planes = new List<byte[]>(4);
            for (int i = 0; i < 4; i++)
            {
                planes.Add(GetPlaneData(i));
            }
            return planes;
        }
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct HWDecoderStats
    {
        public long frames_decoded;     // 已解码帧数
        public long frames_dropped;     // 丢弃帧数
        public long frames_corrupted;   // 损坏帧数
        public long bytes_decoded;      // 已解码字节数
        public double decode_time_ms;   // 解码时间 (毫秒)
        public double fps;              // 当前帧率
        public int buffer_level;        // 缓冲区级别 (0-100)
        public long current_bitrate;    // 当前比特率
        public long average_bitrate;    // 平均比特率
        public long max_bitrate;        // 最大比特率
        public long min_bitrate;        // 最小比特率
        public long peak_bitrate;       // 峰值比特率
        public long total_delay;        // 总延迟
        public long current_delay;      // 当前延迟
        public long max_delay;          // 最大延迟
        public long min_delay;          // 最小延迟
        public long average_delay;      // 平均延迟
    }
    
    // 回调委托定义
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HWFrameCallback(IntPtr user_data, IntPtr frame);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HWErrorCallback(IntPtr user_data, HWDecoderError error, [MarshalAs(UnmanagedType.LPStr)] string message);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HWFormatChangedCallback(IntPtr user_data, IntPtr config);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HWBufferCallback(IntPtr user_data, int buffer_level, int buffer_capacity);
    
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void HWLogCallback(IntPtr user_data, HWLogLevel level, [MarshalAs(UnmanagedType.LPStr)] string message);
    
    [StructLayout(LayoutKind.Sequential)]
    public struct HWCallbacks
    {
        public IntPtr frame_callback;            // HWFrameCallback
        public IntPtr error_callback;            // HWErrorCallback
        public IntPtr format_changed_callback;   // HWFormatChangedCallback
        public IntPtr buffer_callback;           // HWBufferCallback
        public IntPtr user_data;
    }
    
    // ===================== 原生API声明 =====================
    
    [SuppressUnmanagedCodeSecurity]
    public static unsafe class HardwareDecoderNative
    {
        private const string LibraryName = "hardware_decoder";
        
        // 创建解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hw_decoder_create(HWCodecType codec_type, ref HWDecoderConfig config, ref HWCallbacks callbacks);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hw_decoder_create_simple(HWCodecType codec_type, int width, int height, [MarshalAs(UnmanagedType.I1)] bool use_hardware);
        
        // 销毁解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_destroy(IntPtr handle);
        
        // 解码数据
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_decode(IntPtr handle, byte[] data, int size, long pts, long dts, ref HWFrameData frame_data);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_decode_simple(IntPtr handle, byte[] data, int size, ref HWFrameData frame_data);
        
        // 刷新解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_flush(IntPtr handle, ref HWFrameData frame_data);
        
        // 重置解码器
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_reset(IntPtr handle);
        
        // 配置管理
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_get_config(IntPtr handle, ref HWDecoderConfig config);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_update_config(IntPtr handle, ref HWDecoderConfig config);
        
        // 统计信息
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_get_stats(IntPtr handle, ref HWDecoderStats stats);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_reset_stats(IntPtr handle);
        
        // 硬件加速检查
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool hw_decoder_is_hardware_supported(HWCodecType codec_type);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool hw_decoder_is_hardware_accelerated(IntPtr handle);
        
        // 信息获取
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_hardware_type(IntPtr handle);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_codec_name(IntPtr handle);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_pixel_format_name(HWPixelFormat format);
        
        // 日志管理
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_set_log_callback(IntPtr callback, IntPtr user_data);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_set_log_level(HWLogLevel level);
        
        // 版本信息
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_version();
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_build_info();
        
        // 帧内存管理
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hw_decoder_allocate_frame();
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_free_frame(IntPtr frame);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_copy_frame(IntPtr src, IntPtr dst);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_get_plane_size(IntPtr frame, int plane);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_get_frame_size(IntPtr frame);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool hw_decoder_is_frame_valid(IntPtr frame);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_clear_frame(IntPtr frame);
        
        // 属性管理
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_set_property(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_property(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name);
        
        // 错误信息
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string hw_decoder_get_error_string(int error_code);
        
        // 初始化和清理
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_initialize();
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_cleanup();
        
        // 线程安全
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_lock(IntPtr handle);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_unlock(IntPtr handle);
        
        // 缓存管理
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_set_max_cache_frames(IntPtr handle, int max_frames);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_get_cache_frame_count(IntPtr handle);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hw_decoder_clear_cache(IntPtr handle);
        
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hw_decoder_get_cached_frame(IntPtr handle, ref HWFrameData frame_data, int index);
        
        // 更多API函数...
        // 注意：为了简洁，这里省略了一些辅助函数，实际使用时需要根据需求添加
    }
    
    // ===================== 高级包装类 =====================
    
    public class HardwareDecoder : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;
        private HWCodecType _codecType;
        private HWDecoderConfig _config;
        private HWDecoderStats _stats;
        private HWLogCallback _logCallback;
        private GCHandle _logCallbackHandle;
        
        // 事件
        public event Action<HWFrameData> FrameDecoded;
        public event Action<HWDecoderError, string> ErrorOccurred;
        public event Action<HWDecoderConfig> FormatChanged;
        public event Action<int, int> BufferStatusChanged;
        
        // 属性
        public IntPtr Handle => _handle;
        public HWCodecType CodecType => _codecType;
        public HWDecoderConfig Config => _config;
        public HWDecoderStats Stats => _stats;
        public bool IsDisposed => _disposed;
        public bool IsHardwareAccelerated => HardwareDecoderNative.hw_decoder_is_hardware_accelerated(_handle);
        public string HardwareType => HardwareDecoderNative.hw_decoder_get_hardware_type(_handle);
        public string CodecName => HardwareDecoderNative.hw_decoder_get_codec_name(_handle);
        
        // 日志回调包装
        private void OnLogMessage(IntPtr userData, HWLogLevel level, string message)
        {
            try
            {
                switch (level)
                {
                    case HWLogLevel.HW_LOG_PANIC:
                    case HWLogLevel.HW_LOG_FATAL:
                    case HWLogLevel.HW_LOG_ERROR:
                        Logger.Error?.Print(LogClass.FFmpeg, $"[HardwareDecoder] {message}");
                        break;
                    case HWLogLevel.HW_LOG_WARNING:
                        Logger.Warning?.Print(LogClass.FFmpeg, $"[HardwareDecoder] {message}");
                        break;
                    case HWLogLevel.HW_LOG_INFO:
                        Logger.Info?.Print(LogClass.FFmpeg, $"[HardwareDecoder] {message}");
                        break;
                    case HWLogLevel.HW_LOG_VERBOSE:
                    case HWLogLevel.HW_LOG_DEBUG:
                        Logger.Debug?.Print(LogClass.FFmpeg, $"[HardwareDecoder] {message}");
                        break;
                    case HWLogLevel.HW_LOG_TRACE:
                        Logger.Trace?.Print(LogClass.FFmpeg, $"[HardwareDecoder] {message}");
                        break;
                }
            }
            catch
            {
                // 忽略日志回调中的异常
            }
        }
        
        // 构造函数
        public HardwareDecoder(HWCodecType codecType, HWDecoderConfig? config = null, bool useHardware = true)
        {
            _codecType = codecType;
            _disposed = false;
            
            // 设置日志回调
            _logCallback = new HWLogCallback(OnLogMessage);
            _logCallbackHandle = GCHandle.Alloc(_logCallback);
            IntPtr logCallbackPtr = Marshal.GetFunctionPointerForDelegate(_logCallback);
            HardwareDecoderNative.hw_decoder_set_log_callback(logCallbackPtr, IntPtr.Zero);
            
            // 设置日志级别
            HardwareDecoderNative.hw_decoder_set_log_level(HWLogLevel.HW_LOG_INFO);
            
            // 初始化子系统
            int initResult = HardwareDecoderNative.hw_decoder_initialize();
            if (initResult != (int)HWDecoderError.HW_DECODER_SUCCESS)
            {
                throw new InvalidOperationException($"Failed to initialize hardware decoder subsystem: {GetErrorString(initResult)}");
            }
            
            // 创建解码器
            if (config.HasValue)
            {
                _config = config.Value;
                HWCallbacks callbacks = CreateCallbacks();
                _handle = HardwareDecoderNative.hw_decoder_create(codecType, ref _config, ref callbacks);
            }
            else
            {
                // 使用默认配置
                _config = new HWDecoderConfig(1920, 1080);
                _handle = HardwareDecoderNative.hw_decoder_create_simple(codecType, 1920, 1080, useHardware);
            }
            
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create hardware decoder instance");
            }
            
            // 初始化统计信息
            _stats = new HWDecoderStats();
            
            Logger.Info?.Print(LogClass.FFmpeg, $"Hardware decoder created: Type={codecType}, Hardware={useHardware}, Handle=0x{_handle:X}");
        }
        
        // 创建回调结构
        private HWCallbacks CreateCallbacks()
        {
            HWCallbacks callbacks = new HWCallbacks();
            
            // 帧回调
            if (FrameDecoded != null)
            {
                HWFrameCallback frameCallback = (userData, framePtr) =>
                {
                    try
                    {
                        HWFrameData frameData = Marshal.PtrToStructure<HWFrameData>(framePtr);
                        FrameDecoded?.Invoke(frameData);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"Frame callback error: {ex.Message}");
                    }
                };
                callbacks.frame_callback = Marshal.GetFunctionPointerForDelegate(frameCallback);
            }
            
            // 错误回调
            if (ErrorOccurred != null)
            {
                HWErrorCallback errorCallback = (userData, error, message) =>
                {
                    try
                    {
                        ErrorOccurred?.Invoke(error, message);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"Error callback error: {ex.Message}");
                    }
                };
                callbacks.error_callback = Marshal.GetFunctionPointerForDelegate(errorCallback);
            }
            
            // 格式改变回调
            if (FormatChanged != null)
            {
                HWFormatChangedCallback formatCallback = (userData, configPtr) =>
                {
                    try
                    {
                        HWDecoderConfig config = Marshal.PtrToStructure<HWDecoderConfig>(configPtr);
                        FormatChanged?.Invoke(config);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"Format changed callback error: {ex.Message}");
                    }
                };
                callbacks.format_changed_callback = Marshal.GetFunctionPointerForDelegate(formatCallback);
            }
            
            // 缓冲区状态回调
            if (BufferStatusChanged != null)
            {
                HWBufferCallback bufferCallback = (userData, bufferLevel, bufferCapacity) =>
                {
                    try
                    {
                        BufferStatusChanged?.Invoke(bufferLevel, bufferCapacity);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error?.Print(LogClass.FFmpeg, $"Buffer callback error: {ex.Message}");
                    }
                };
                callbacks.buffer_callback = Marshal.GetFunctionPointerForDelegate(bufferCallback);
            }
            
            callbacks.user_data = IntPtr.Zero;
            
            return callbacks;
        }
        
        // 解码数据
        public HWDecoderError Decode(byte[] data, long pts, long dts, out HWFrameData frameData)
        {
            frameData = new HWFrameData();
            
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            if (data == null || data.Length == 0)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_PARAMETER;
            }
            
            int result = HardwareDecoderNative.hw_decoder_decode(_handle, data, data.Length, pts, dts, ref frameData);
            return (HWDecoderError)result;
        }
        
        // 简化解码
        public HWDecoderError Decode(byte[] data, out HWFrameData frameData)
        {
            long timestamp = DateTime.UtcNow.Ticks / 10; // 转换为微秒
            return Decode(data, timestamp, timestamp, out frameData);
        }
        
        // 解码数据段
        public HWDecoderError Decode(ArraySegment<byte> data, long pts, long dts, out HWFrameData frameData)
        {
            byte[] buffer = new byte[data.Count];
            Array.Copy(data.Array, data.Offset, buffer, 0, data.Count);
            return Decode(buffer, pts, dts, out frameData);
        }
        
        // 刷新解码器
        public HWDecoderError Flush(out HWFrameData frameData)
        {
            frameData = new HWFrameData();
            
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            int result = HardwareDecoderNative.hw_decoder_flush(_handle, ref frameData);
            return (HWDecoderError)result;
        }
        
        // 重置解码器
        public void Reset()
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            
            HardwareDecoderNative.hw_decoder_reset(_handle);
            Logger.Debug?.Print(LogClass.FFmpeg, "Hardware decoder reset");
        }
        
        // 获取配置
        public HWDecoderError GetConfig(out HWDecoderConfig config)
        {
            config = new HWDecoderConfig();
            
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            int result = HardwareDecoderNative.hw_decoder_get_config(_handle, ref config);
            _config = config;
            return (HWDecoderError)result;
        }
        
        // 更新配置
        public HWDecoderError UpdateConfig(HWDecoderConfig config)
        {
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            int result = HardwareDecoderNative.hw_decoder_update_config(_handle, ref config);
            if (result == (int)HWDecoderError.HW_DECODER_SUCCESS)
            {
                _config = config;
            }
            return (HWDecoderError)result;
        }
        
        // 获取统计信息
        public HWDecoderError GetStats(out HWDecoderStats stats)
        {
            stats = new HWDecoderStats();
            
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            int result = HardwareDecoderNative.hw_decoder_get_stats(_handle, ref stats);
            _stats = stats;
            return (HWDecoderError)result;
        }
        
        // 重置统计信息
        public void ResetStats()
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            
            HardwareDecoderNative.hw_decoder_reset_stats(_handle);
            _stats = new HWDecoderStats();
        }
        
        // 获取错误字符串
        public static string GetErrorString(int errorCode)
        {
            IntPtr errorStringPtr = Marshal.StringToHGlobalAnsi(HardwareDecoderNative.hw_decoder_get_error_string(errorCode));
            string errorString = Marshal.PtrToStringAnsi(errorStringPtr);
            Marshal.FreeHGlobal(errorStringPtr);
            return errorString ?? "Unknown error";
        }
        
        public static string GetErrorString(HWDecoderError error)
        {
            return GetErrorString((int)error);
        }
        
        // 检查硬件支持
        public static bool IsHardwareSupported(HWCodecType codecType)
        {
            return HardwareDecoderNative.hw_decoder_is_hardware_supported(codecType);
        }
        
        // 获取版本信息
        public static string GetVersion()
        {
            return HardwareDecoderNative.hw_decoder_get_version();
        }
        
        public static string GetBuildInfo()
        {
            return HardwareDecoderNative.hw_decoder_get_build_info();
        }
        
        // 缓存管理
        public int CacheFrameCount
        {
            get
            {
                if (_disposed || _handle == IntPtr.Zero)
                    return 0;
                
                return HardwareDecoderNative.hw_decoder_get_cache_frame_count(_handle);
            }
        }
        
        public HWDecoderError SetMaxCacheFrames(int maxFrames)
        {
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            if (maxFrames <= 0)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_PARAMETER;
            }
            
            int result = HardwareDecoderNative.hw_decoder_set_max_cache_frames(_handle, maxFrames);
            return (HWDecoderError)result;
        }
        
        public void ClearCache()
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            
            HardwareDecoderNative.hw_decoder_clear_cache(_handle);
        }
        
        public HWDecoderError GetCachedFrame(int index, out HWFrameData frameData)
        {
            frameData = new HWFrameData();
            
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            if (index < 0)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_PARAMETER;
            }
            
            int result = HardwareDecoderNative.hw_decoder_get_cached_frame(_handle, ref frameData, index);
            return (HWDecoderError)result;
        }
        
        // 线程安全
        public void Lock()
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            
            HardwareDecoderNative.hw_decoder_lock(_handle);
        }
        
        public void Unlock()
        {
            if (_disposed || _handle == IntPtr.Zero)
                return;
            
            HardwareDecoderNative.hw_decoder_unlock(_handle);
        }
        
        // 属性管理
        public HWDecoderError SetProperty(string name, string value)
        {
            if (_disposed || _handle == IntPtr.Zero)
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_HANDLE;
            }
            
            if (string.IsNullOrEmpty(name))
            {
                return HWDecoderError.HW_DECODER_ERROR_INVALID_PARAMETER;
            }
            
            int result = HardwareDecoderNative.hw_decoder_set_property(_handle, name, value);
            return (HWDecoderError)result;
        }
        
        public string GetProperty(string name)
        {
            if (_disposed || _handle == IntPtr.Zero || string.IsNullOrEmpty(name))
                return null;
            
            return HardwareDecoderNative.hw_decoder_get_property(_handle, name);
        }
        
        // 资源清理
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 释放托管资源
                    FrameDecoded = null;
                    ErrorOccurred = null;
                    FormatChanged = null;
                    BufferStatusChanged = null;
                }
                
                // 释放非托管资源
                if (_handle != IntPtr.Zero)
                {
                    HardwareDecoderNative.hw_decoder_destroy(_handle);
                    _handle = IntPtr.Zero;
                    Logger.Info?.Print(LogClass.FFmpeg, "Hardware decoder destroyed");
                }
                
                // 释放日志回调
                if (_logCallbackHandle.IsAllocated)
                {
                    _logCallbackHandle.Free();
                }
                
                // 清理子系统
                HardwareDecoderNative.hw_decoder_cleanup();
                
                _disposed = true;
            }
        }
        
        ~HardwareDecoder()
        {
            Dispose(false);
        }
        
        // 静态辅助方法
        public static void SetGlobalLogLevel(HWLogLevel level)
        {
            HardwareDecoderNative.hw_decoder_set_log_level(level);
        }
        
        public static string GetPixelFormatName(HWPixelFormat format)
        {
            return HardwareDecoderNative.hw_decoder_get_pixel_format_name(format);
        }
        
        // 创建H264解码器
        public static HardwareDecoder CreateH264Decoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return new HardwareDecoder(HWCodecType.HW_CODEC_H264, 
                new HWDecoderConfig(width, height), useHardware);
        }
        
        // 创建VP8解码器
        public static HardwareDecoder CreateVP8Decoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return new HardwareDecoder(HWCodecType.HW_CODEC_VP8, 
                new HWDecoderConfig(width, height), useHardware);
        }
        
        // 创建VP9解码器
        public static HardwareDecoder CreateVP9Decoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return new HardwareDecoder(HWCodecType.HW_CODEC_VP9, 
                new HWDecoderConfig(width, height), useHardware);
        }
        
        // 创建HEVC解码器
        public static HardwareDecoder CreateHEVCDecoder(int width = 1920, int height = 1080, bool useHardware = true)
        {
            return new HardwareDecoder(HWCodecType.HW_CODEC_HEVC, 
                new HWDecoderConfig(width, height), useHardware);
        }
    }
}
