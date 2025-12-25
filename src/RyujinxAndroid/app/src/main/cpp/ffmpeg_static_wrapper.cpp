// ffmpeg_static_wrapper.cpp
// FFmpeg 静态库包装器，为 C# P/Invoke 提供稳定的 C ABI
#include <android/log.h>
#include <string>
#include <mutex>

// 先检查是否定义了 NO_FFMPEG，如果没有定义再包含 FFmpeg 头文件
#ifndef NO_FFMPEG

// 使用 extern "C" 包装所有 FFmpeg 头文件包含
extern "C" {
// 包含所有需要的 FFmpeg 头文件
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/frame.h>
#include <libavutil/imgutils.h>
#include <libavutil/opt.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersrc.h>
#include <libavfilter/buffersink.h>
}

#endif // NO_FFMPEG

#define LOG_TAG "FFmpegStatic"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

// 全局初始化标志
static std::once_flag ffmpeg_init_flag;

#ifndef NO_FFMPEG

// 自定义日志回调
static void ffmpeg_log_callback(void* ptr, int level, const char* fmt, va_list vl) {
    if (level > av_log_get_level()) return;
    
    char line[1024];
    static int print_prefix = 1;
    av_log_format_line(ptr, level, fmt, vl, line, sizeof(line), &print_prefix);
    
    switch (level) {
        case AV_LOG_PANIC:
        case AV_LOG_FATAL:
        case AV_LOG_ERROR:
            LOGE("%s", line);
            break;
        case AV_LOG_WARNING:
            LOGW("%s", line);
            break;
        case AV_LOG_INFO:
            LOGI("%s", line);
            break;
        case AV_LOG_VERBOSE:
        case AV_LOG_DEBUG:
        case AV_LOG_TRACE:
            LOGD("%s", line);
            break;
    }
}

// 初始化 FFmpeg
static void initialize_ffmpeg() {
    // 设置日志回调
    av_log_set_callback(ffmpeg_log_callback);
    
    // 根据构建类型设置日志级别
#ifdef NDEBUG
    av_log_set_level(AV_LOG_WARNING);
#else
    av_log_set_level(AV_LOG_VERBOSE);
#endif
    
    LOGI("FFmpeg static wrapper initialized");
}

#endif // NO_FFMPEG

// ==================== C 函数导出，供 C# P/Invoke 直接调用 ====================

extern "C" {

// 初始化函数 - 使用 constructor 属性自动调用
__attribute__((constructor)) void ffmpeg_auto_init() {
#ifdef NO_FFMPEG
    LOGW("FFmpeg not available (NO_FFMPEG defined)");
#else
    std::call_once(ffmpeg_init_flag, initialize_ffmpeg);
#endif
}

// 手动初始化函数（可选）
void ffmpeg_init() {
#ifdef NO_FFMPEG
    LOGW("FFmpeg not available (NO_FFMPEG defined)");
#else
    std::call_once(ffmpeg_init_flag, initialize_ffmpeg);
#endif
}

// 版本信息
const char* ffmpeg_version() {
#ifdef NO_FFMPEG
    return "FFmpeg not available";
#else
    return av_version_info();
#endif
}

int ffmpeg_avcodec_version() {
#ifdef NO_FFMPEG
    return 0;
#else
    return avcodec_version();
#endif
}

int ffmpeg_avutil_version() {
#ifdef NO_FFMPEG
    return 0;
#else
    return avutil_version();
#endif
}

int ffmpeg_avformat_version() {
#ifdef NO_FFMPEG
    return 0;
#else
    return avformat_version();
#endif
}

#ifndef NO_FFMPEG

// C# 代码需要的函数 - 使用弱符号避免冲突
__attribute__((weak)) AVFrame* av_frame_alloc() {
    return ::av_frame_alloc();
}

__attribute__((weak)) void av_frame_unref(AVFrame* frame) {
    ::av_frame_unref(frame);
}

__attribute__((weak)) const AVCodec* avcodec_find_decoder(enum AVCodecID id) {
    return ::avcodec_find_decoder(id);
}

__attribute__((weak)) AVCodecContext* avcodec_alloc_context3(const AVCodec* codec) {
    return ::avcodec_alloc_context3(codec);
}

__attribute__((weak)) int avcodec_open2(AVCodecContext* avctx, const AVCodec* codec, AVDictionary** options) {
    return ::avcodec_open2(avctx, codec, options);
}

__attribute__((weak)) int avcodec_close(AVCodecContext* avctx) {
    return ::avcodec_close(avctx);
}

__attribute__((weak)) void avcodec_free_context(AVCodecContext** avctx) {
    ::avcodec_free_context(avctx);
}

__attribute__((weak)) AVPacket* av_packet_alloc() {
    return ::av_packet_alloc();
}

__attribute__((weak)) void av_packet_unref(AVPacket* pkt) {
    ::av_packet_unref(pkt);
}

__attribute__((weak)) void av_packet_free(AVPacket** pkt) {
    ::av_packet_free(pkt);
}

__attribute__((weak)) void av_free(void* ptr) {
    ::av_free(ptr);
}

__attribute__((weak)) void av_log_set_level(int level) {
    ::av_log_set_level(level);
}

__attribute__((weak)) int av_log_get_level() {
    return ::av_log_get_level();
}

__attribute__((weak)) void av_log_format_line(void* ptr, int level, const char* fmt, va_list vl, char* line, int line_size, int* print_prefix) {
    ::av_log_format_line(ptr, level, fmt, vl, line, line_size, print_prefix);
}

__attribute__((weak)) unsigned avcodec_version() {
    return ::avcodec_version();
}

#else

// 当 NO_FFMPEG 定义时的空实现
void* av_frame_alloc() { return nullptr; }
void av_frame_unref(void* frame) {}
void* avcodec_find_decoder(int id) { return nullptr; }
void* avcodec_alloc_context3(void* codec) { return nullptr; }
int avcodec_open2(void* avctx, void* codec, void** options) { return -1; }
int avcodec_close(void* avctx) { return -1; }
void avcodec_free_context(void** avctx) {}
void* av_packet_alloc() { return nullptr; }
void av_packet_unref(void* pkt) {}
void av_packet_free(void** pkt) {}
void av_free(void* ptr) {}
void av_log_set_level(int level) {}
int av_log_get_level() { return 0; }
void av_log_format_line(void* ptr, int level, const char* fmt, void* vl, char* line, int line_size, int* print_prefix) {}
unsigned avcodec_version() { return 0; }

#endif // NO_FFMPEG

} // extern "C" 结束
