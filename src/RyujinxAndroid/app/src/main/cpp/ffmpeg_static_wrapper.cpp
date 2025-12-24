// ffmpeg_static_wrapper.cpp
// FFmpeg 静态库包装器，为 C# P/Invoke 提供稳定的 C ABI
#include <android/log.h>
#include <string>
#include <mutex>

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

#define LOG_TAG "FFmpegStatic"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

// 全局初始化标志
static std::once_flag ffmpeg_init_flag;

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
    
    LOGI("FFmpeg static wrapper initialized, version: %s", av_version_info());
}

// ==================== C 函数导出，供 C# P/Invoke 直接调用 ====================

extern "C" {

// 初始化函数
void ffmpeg_init() {
    std::call_once(ffmpeg_init_flag, initialize_ffmpeg);
}

// 版本信息
const char* ffmpeg_version() {
    return av_version_info();
}

int ffmpeg_avcodec_version() {
    return avcodec_version();
}

int ffmpeg_avutil_version() {
    return avutil_version();
}

int ffmpeg_avformat_version() {
    return avformat_version();
}

// 注意：以下函数在 FFmpeg 静态库中已定义，不要在这里重新定义
// 只定义 C# 代码中通过 FFmpegApi.cs 直接调用的函数

// avutil 函数（根据 C# 需要定义）
AVFrame* av_frame_alloc() {
    return ::av_frame_alloc();
}

void av_frame_unref(AVFrame* frame) {
    ::av_frame_unref(frame);
}

// avcodec 函数（根据 C# 需要定义）
const AVCodec* avcodec_find_decoder(enum AVCodecID id) {
    return ::avcodec_find_decoder(id);
}

AVCodecContext* avcodec_alloc_context3(const AVCodec* codec) {
    return ::avcodec_alloc_context3(codec);
}

int avcodec_open2(AVCodecContext* avctx, const AVCodec* codec, AVDictionary** options) {
    return ::avcodec_open2(avctx, codec, options);
}

int avcodec_close(AVCodecContext* avctx) {
    return ::avcodec_close(avctx);
}

void avcodec_free_context(AVCodecContext** avctx) {
    ::avcodec_free_context(avctx);
}

AVPacket* av_packet_alloc() {
    return ::av_packet_alloc();
}

void av_packet_unref(AVPacket* pkt) {
    ::av_packet_unref(pkt);
}

void av_packet_free(AVPacket** pkt) {
    ::av_packet_free(pkt);
}

// 修复：avcodec_version 返回 unsigned（根据 FFmpeg 头文件）
unsigned avcodec_version() {
    return ::avcodec_version();
}

} // extern "C" 结束
