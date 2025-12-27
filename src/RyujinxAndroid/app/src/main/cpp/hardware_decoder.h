// hardware_decoder.h
#pragma once

#include <cstdint>

#ifdef __cplusplus
extern "C" {
#endif

// 解码器句柄类型
typedef void* HWDecoderHandle;

// 编解码器类型枚举
enum HWCodecType {
    HW_CODEC_H264 = 0,
    HW_CODEC_VP8 = 1,
    HW_CODEC_VP9 = 2,
    HW_CODEC_HEVC = 3,
    HW_CODEC_AV1 = 4
};

// 像素格式枚举
enum HWPixelFormat {
    HW_PIX_FMT_NONE = -1,
    HW_PIX_FMT_YUV420P = 0,
    HW_PIX_FMT_NV12 = 1,
    HW_PIX_FMT_NV21 = 2,
    HW_PIX_FMT_RGBA = 3,
    HW_PIX_FMT_BGRA = 4,
    HW_PIX_FMT_ARGB = 5,
    HW_PIX_FMT_ABGR = 6
};

// 解码器配置结构
struct HWDecoderConfig {
    int width;                 // 视频宽度
    int height;                // 视频高度
    int bit_depth;             // 位深度 (8, 10, 12)
    int chroma_format;         // 色度格式
    bool low_latency;          // 低延迟模式
    int thread_count;          // 线程数 (0=自动)
    int max_ref_frames;        // 最大参考帧数
    bool enable_deblocking;    // 启用去块滤波
    bool enable_sao;           // 启用SAO滤波
    int profile;               // 编码器档次
    int level;                 // 编码器级别
};

// 帧数据结构
struct HWFrameData {
    uint8_t* data[4];           // 平面数据指针 [Y, U, V, A]
    int linesize[4];            // 每个平面的行大小
    int width;                  // 帧宽度
    int height;                 // 帧高度
    int format;                 // 像素格式 (HWPixelFormat)
    int64_t pts;                // 显示时间戳
    int64_t dts;                // 解码时间戳
    int64_t duration;           // 帧持续时间
    bool key_frame;             // 是否为关键帧
    bool interlaced;            // 是否为隔行扫描
    int repeat_pict;            // 重复图像计数
    int coded_picture_number;   // 编码图像序号
    int display_picture_number; // 显示图像序号
    int quality;                // 图像质量 (1-FF_LAMBDA_MAX)
    int64_t reordered_opaque;   // 重新排序不透明数据
    int sample_aspect_ratio_num;// 采样宽高比分子
    int sample_aspect_ratio_den;// 采样宽高比分母
    int color_range;            // 颜色范围
    int color_primaries;        // 颜色原色
    int color_trc;              // 颜色传输特性
    int colorspace;             // 颜色空间
    int chroma_location;        // 色度位置
    int best_effort_timestamp;  // 尽力而为时间戳
    int pkt_pos;                // 包位置
    int pkt_size;               // 包大小
    int metadata_count;         // 元数据计数
    void** metadata;            // 元数据指针数组
    int decode_error_flags;     // 解码错误标志
    int channels;               // 音频通道数
    int channel_layout;         // 音频通道布局
    int nb_samples;             // 音频采样数
    int sample_rate;            // 音频采样率
    int audio_channels;         // 音频通道数 (已弃用，使用channels)
    int audio_channel_layout;   // 音频通道布局 (已弃用，使用channel_layout)
    int audio_sample_rate;      // 音频采样率 (已弃用，使用sample_rate)
    int audio_sample_format;    // 音频采样格式
    int audio_frame_size;       // 音频帧大小
    int audio_buffer_size;      // 音频缓冲区大小
};

// 解码器统计信息
struct HWDecoderStats {
    int64_t frames_decoded;     // 已解码帧数
    int64_t frames_dropped;     // 丢弃帧数
    int64_t frames_corrupted;   // 损坏帧数
    int64_t bytes_decoded;      // 已解码字节数
    double decode_time_ms;      // 解码时间 (毫秒)
    double fps;                 // 当前帧率
    int buffer_level;           // 缓冲区级别 (0-100)
    int64_t current_bitrate;    // 当前比特率
    int64_t average_bitrate;    // 平均比特率
    int64_t max_bitrate;        // 最大比特率
    int64_t min_bitrate;        // 最小比特率
    int64_t peak_bitrate;       // 峰值比特率
    int64_t total_delay;        // 总延迟
    int64_t current_delay;      // 当前延迟
    int64_t max_delay;          // 最大延迟
    int64_t min_delay;          // 最小延迟
    int64_t average_delay;      // 平均延迟
};

// 错误码枚举
enum HWDecoderError {
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
};

// 日志级别枚举
enum HWLogLevel {
    HW_LOG_QUIET = -8,
    HW_LOG_PANIC = 0,
    HW_LOG_FATAL = 8,
    HW_LOG_ERROR = 16,
    HW_LOG_WARNING = 24,
    HW_LOG_INFO = 32,
    HW_LOG_VERBOSE = 40,
    HW_LOG_DEBUG = 48,
    HW_LOG_TRACE = 56
};

// 日志回调函数类型
typedef void (*HWLogCallback)(void* user_data, HWLogLevel level, const char* message);

// 解码器回调函数类型
typedef void (*HWFrameCallback)(void* user_data, const HWFrameData* frame);
typedef void (*HWErrorCallback)(void* user_data, HWDecoderError error, const char* message);
typedef void (*HWFormatChangedCallback)(void* user_data, const HWDecoderConfig* config);
typedef void (*HWBufferCallback)(void* user_data, int buffer_level, int buffer_capacity);

// 回调结构
struct HWCallbacks {
    HWFrameCallback frame_callback;
    HWErrorCallback error_callback;
    HWFormatChangedCallback format_changed_callback;
    HWBufferCallback buffer_callback;
    void* user_data;
};

// ===================== 主要API函数 =====================

// 创建解码器实例
HWDecoderHandle hw_decoder_create(HWCodecType codec_type, const HWDecoderConfig* config, const HWCallbacks* callbacks);

// 创建解码器实例 (简化版)
HWDecoderHandle hw_decoder_create_simple(HWCodecType codec_type, int width, int height, bool use_hardware);

// 销毁解码器实例
void hw_decoder_destroy(HWDecoderHandle handle);

// 解码数据
int hw_decoder_decode(HWDecoderHandle handle, const uint8_t* data, int size, int64_t pts, int64_t dts, HWFrameData* frame_data);

// 解码数据 (简化版)
int hw_decoder_decode_simple(HWDecoderHandle handle, const uint8_t* data, int size, HWFrameData* frame_data);

// 刷新解码器 (处理剩余帧)
int hw_decoder_flush(HWDecoderHandle handle, HWFrameData* frame_data);

// 重置解码器
void hw_decoder_reset(HWDecoderHandle handle);

// 获取解码器配置
int hw_decoder_get_config(HWDecoderHandle handle, HWDecoderConfig* config);

// 更新解码器配置
int hw_decoder_update_config(HWDecoderHandle handle, const HWDecoderConfig* config);

// 获取解码器统计信息
int hw_decoder_get_stats(HWDecoderHandle handle, HWDecoderStats* stats);

// 重置解码器统计信息
void hw_decoder_reset_stats(HWDecoderHandle handle);

// 检查是否支持硬件解码
bool hw_decoder_is_hardware_supported(HWCodecType codec_type);

// 检查解码器是否使用硬件加速
bool hw_decoder_is_hardware_accelerated(HWDecoderHandle handle);

// 获取硬件设备类型
const char* hw_decoder_get_hardware_type(HWDecoderHandle handle);

// 获取解码器名称
const char* hw_decoder_get_codec_name(HWDecoderHandle handle);

// 获取像素格式名称
const char* hw_decoder_get_pixel_format_name(HWPixelFormat format);

// 设置日志回调
void hw_decoder_set_log_callback(HWLogCallback callback, void* user_data);

// 设置日志级别
void hw_decoder_set_log_level(HWLogLevel level);

// 获取版本信息
const char* hw_decoder_get_version(void);

// 获取构建信息
const char* hw_decoder_get_build_info(void);

// 分配帧数据内存
HWFrameData* hw_decoder_allocate_frame(void);

// 释放帧数据内存
void hw_decoder_free_frame(HWFrameData* frame);

// 复制帧数据
int hw_decoder_copy_frame(const HWFrameData* src, HWFrameData* dst);

// 获取帧数据平面大小
int hw_decoder_get_plane_size(const HWFrameData* frame, int plane);

// 获取帧数据总大小
int hw_decoder_get_frame_size(const HWFrameData* frame);

// 检查帧数据是否有效
bool hw_decoder_is_frame_valid(const HWFrameData* frame);

// 清除帧数据
void hw_decoder_clear_frame(HWFrameData* frame);

// 设置解码器属性
int hw_decoder_set_property(HWDecoderHandle handle, const char* name, const char* value);

// 获取解码器属性
const char* hw_decoder_get_property(HWDecoderHandle handle, const char* name);

// 获取支持的编解码器列表
int hw_decoder_get_supported_codecs(HWCodecType** codecs, int* count);

// 释放编解码器列表
void hw_decoder_free_supported_codecs(HWCodecType* codecs);

// 获取支持的像素格式列表
int hw_decoder_get_supported_formats(HWPixelFormat** formats, int* count);

// 释放像素格式列表
void hw_decoder_free_supported_formats(HWPixelFormat* formats);

// 获取错误信息
const char* hw_decoder_get_error_string(int error_code);

// 初始化硬件解码器子系统
int hw_decoder_initialize(void);

// 清理硬件解码器子系统
void hw_decoder_cleanup(void);

// 线程安全锁定
void hw_decoder_lock(HWDecoderHandle handle);

// 线程安全解锁
void hw_decoder_unlock(HWDecoderHandle handle);

// 设置最大缓存帧数
int hw_decoder_set_max_cache_frames(HWDecoderHandle handle, int max_frames);

// 获取当前缓存帧数
int hw_decoder_get_cache_frame_count(HWDecoderHandle handle);

// 清空缓存帧
void hw_decoder_clear_cache(HWDecoderHandle handle);

// 从缓存获取帧
int hw_decoder_get_cached_frame(HWDecoderHandle handle, HWFrameData* frame_data, int index);

// 设置时基
int hw_decoder_set_time_base(HWDecoderHandle handle, int numerator, int denominator);

// 获取时基
int hw_decoder_get_time_base(HWDecoderHandle handle, int* numerator, int* denominator);

// 设置帧率
int hw_decoder_set_frame_rate(HWDecoderHandle handle, int numerator, int denominator);

// 获取帧率
int hw_decoder_get_frame_rate(HWDecoderHandle handle, int* numerator, int* denominator);

// 设置显示宽高比
int hw_decoder_set_aspect_ratio(HWDecoderHandle handle, int numerator, int denominator);

// 获取显示宽高比
int hw_decoder_get_aspect_ratio(HWDecoderHandle handle, int* numerator, int* denominator);

// 设置色彩信息
int hw_decoder_set_color_info(HWDecoderHandle handle, int color_range, int color_primaries, int color_trc, int colorspace);

// 获取色彩信息
int hw_decoder_get_color_info(HWDecoderHandle handle, int* color_range, int* color_primaries, int* color_trc, int* colorspace);

// 设置音频参数
int hw_decoder_set_audio_params(HWDecoderHandle handle, int sample_rate, int channels, int sample_format);

// 获取音频参数
int hw_decoder_get_audio_params(HWDecoderHandle handle, int* sample_rate, int* channels, int* sample_format);

// 解码音频数据
int hw_decoder_decode_audio(HWDecoderHandle handle, const uint8_t* data, int size, int64_t pts, HWFrameData* frame_data);

// 解码音频数据 (简化版)
int hw_decoder_decode_audio_simple(HWDecoderHandle handle, const uint8_t* data, int size, HWFrameData* frame_data);

// 刷新音频解码器
int hw_decoder_flush_audio(HWDecoderHandle handle, HWFrameData* frame_data);

// 检查是否为音频解码器
bool hw_decoder_is_audio(HWDecoderHandle handle);

// 检查是否为视频解码器
bool hw_decoder_is_video(HWDecoderHandle handle);

// 获取解码延迟
int hw_decoder_get_decode_latency(HWDecoderHandle handle);

// 获取显示延迟
int hw_decoder_get_display_latency(HWDecoderHandle handle);

// 获取总延迟
int hw_decoder_get_total_latency(HWDecoderHandle handle);

// 设置低延迟模式
int hw_decoder_set_low_latency_mode(HWDecoderHandle handle, bool enable);

// 获取低延迟模式状态
bool hw_decoder_get_low_latency_mode(HWDecoderHandle handle);

// 设置丢帧策略
int hw_decoder_set_drop_frame_policy(HWDecoderHandle handle, bool drop_late_frames, bool drop_corrupted_frames);

// 获取丢帧策略
int hw_decoder_get_drop_frame_policy(HWDecoderHandle handle, bool* drop_late_frames, bool* drop_corrupted_frames);

// 设置性能模式
int hw_decoder_set_performance_mode(HWDecoderHandle handle, int mode);

// 获取性能模式
int hw_decoder_get_performance_mode(HWDecoderHandle handle);

// 设置电源模式
int hw_decoder_set_power_mode(HWDecoderHandle handle, int mode);

// 获取电源模式
int hw_decoder_get_power_mode(HWDecoderHandle handle);

// 设置温度限制
int hw_decoder_set_temperature_limit(HWDecoderHandle handle, int temperature_celsius);

// 获取当前温度
int hw_decoder_get_current_temperature(HWDecoderHandle handle);

// 设置缓冲区大小
int hw_decoder_set_buffer_size(HWDecoderHandle handle, int input_buffer_size, int output_buffer_size);

// 获取缓冲区大小
int hw_decoder_get_buffer_size(HWDecoderHandle handle, int* input_buffer_size, int* output_buffer_size);

// 获取缓冲区使用情况
int hw_decoder_get_buffer_usage(HWDecoderHandle handle, int* input_usage, int* output_usage);

// 等待缓冲区可用
int hw_decoder_wait_for_buffer(HWDecoderHandle handle, int buffer_type, int timeout_ms);

// 检查缓冲区是否可用
bool hw_decoder_is_buffer_available(HWDecoderHandle handle, int buffer_type);

// 获取支持的硬件设备列表
int hw_decoder_get_supported_hardware_devices(const char*** devices, int* count);

// 释放硬件设备列表
void hw_decoder_free_supported_hardware_devices(const char** devices);

// 设置硬件设备
int hw_decoder_set_hardware_device(HWDecoderHandle handle, const char* device_name);

// 获取当前硬件设备
const char* hw_decoder_get_hardware_device(HWDecoderHandle handle);

// 获取硬件设备能力
int hw_decoder_get_hardware_capabilities(HWDecoderHandle handle, const char* capability_name, int* value);

// 检查硬件设备特性
bool hw_decoder_check_hardware_feature(HWDecoderHandle handle, const char* feature_name);

// 获取硬件设备信息
const char* hw_decoder_get_hardware_info(HWDecoderHandle handle, const char* info_name);

// 重启硬件设备
int hw_decoder_restart_hardware_device(HWDecoderHandle handle);

// 硬件设备诊断
int hw_decoder_diagnose_hardware_device(HWDecoderHandle handle, char* result, int result_size);

// 设置硬件设备参数
int hw_decoder_set_hardware_parameter(HWDecoderHandle handle, const char* param_name, const char* param_value);

// 获取硬件设备参数
const char* hw_decoder_get_hardware_parameter(HWDecoderHandle handle, const char* param_name);

// 开始性能监控
int hw_decoder_start_performance_monitoring(HWDecoderHandle handle);

// 停止性能监控
int hw_decoder_stop_performance_monitoring(HWDecoderHandle handle);

// 获取性能监控数据
int hw_decoder_get_performance_data(HWDecoderHandle handle, void* data, int data_size);

// 重置性能监控
int hw_decoder_reset_performance_monitoring(HWDecoderHandle handle);

#ifdef __cplusplus
}
#endif
