// ffmpeg_static_wrapper.cpp
#include <android/log.h>

extern "C" {
// 包含所有需要的 FFmpeg 头文件
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/frame.h>
#include <libavutil/imgutils.h>
#include <libswscale/swscale.h>
#include <libswresample/swresample.h>
}

#define LOG_TAG "FFmpegStatic"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)

// ==================== C 函数导出，供 C# P/Invoke 直接调用 ====================

extern "C" {

// avutil 函数
AVFrame* av_frame_alloc() {
    return ::av_frame_alloc();
}

void av_frame_unref(AVFrame* frame) {
    ::av_frame_unref(frame);
}

void av_free(void* ptr) {
    ::av_free(ptr);
}

void av_log_set_level(int level) {
    ::av_log_set_level(level);
}

int av_log_get_level() {
    return ::av_log_get_level();
}

void av_log_set_callback(void (*callback)(void*, int, const char*, va_list)) {
    ::av_log_set_callback(callback);
}

void av_log_format_line(void* ptr, int level, const char* fmt, va_list vl,
                       char* line, int line_size, int* print_prefix) {
    ::av_log_format_line(ptr, level, fmt, vl, line, line_size, print_prefix);
}

// avcodec 函数
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

int avcodec_version() {
    return ::avcodec_version();
}

// swresample 函数（如果需要）
struct SwrContext* swr_alloc() {
    return ::swr_alloc();
}

int swr_init(struct SwrContext* s) {
    return ::swr_init(s);
}

void swr_free(struct SwrContext** s) {
    ::swr_free(s);
}

// swscale 函数（如果需要）
struct SwsContext* sws_getContext(int srcW, int srcH, enum AVPixelFormat srcFormat,
                                  int dstW, int dstH, enum AVPixelFormat dstFormat,
                                  int flags, void* srcFilter,
                                  void* dstFilter, const double* param) {
    return ::sws_getContext(srcW, srcH, srcFormat, dstW, dstH, dstFormat,
                           flags, (SwsFilter*)srcFilter, (SwsFilter*)dstFilter, param);
}

// FFmpeg 初始化函数（如果需要）
void avcodec_register_all() {
    // FFmpeg 6.1.4 不再需要这个函数，但为了兼容性保留
}

void av_register_all() {
    // FFmpeg 6.1.4 不再需要这个函数，但为了兼容性保留
}

} // extern "C" 结束