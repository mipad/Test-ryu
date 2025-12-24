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
    
    // 如果需要，可以在这里注册所有编解码器
    // 注意：在 FFmpeg 6.1.4 中，av_register_all() 和 avcodec_register_all() 已过时
    // 这些函数现在在库初始化时自动调用
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

// avutil 函数
AVFrame* av_frame_alloc() {
    return ::av_frame_alloc();
}

void av_frame_free(AVFrame** frame) {
    ::av_frame_free(frame);
}

void av_frame_unref(AVFrame* frame) {
    ::av_frame_unref(frame);
}

int av_frame_ref(AVFrame* dst, const AVFrame* src) {
    return ::av_frame_ref(dst, src);
}

AVFrame* av_frame_clone(const AVFrame* src) {
    return ::av_frame_clone(src);
}

void av_free(void* ptr) {
    ::av_free(ptr);
}

void av_freep(void* ptr) {
    ::av_freep(ptr);
}

void* av_malloc(size_t size) {
    return ::av_malloc(size);
}

void* av_mallocz(size_t size) {
    return ::av_mallocz(size);
}

void av_log_set_level(int level) {
    ::av_log_set_level(level);
}

int av_log_get_level() {
    return ::av_log_get_level();
}

// 图像处理函数
int av_image_get_buffer_size(enum AVPixelFormat pix_fmt, int width, int height, int align) {
    return ::av_image_get_buffer_size(pix_fmt, width, height, align);
}

int av_image_fill_arrays(uint8_t* dst_data[4], int dst_linesize[4],
                         const uint8_t* src, enum AVPixelFormat pix_fmt,
                         int width, int height, int align) {
    return ::av_image_fill_arrays(dst_data, dst_linesize, src, pix_fmt, width, height, align);
}

// avcodec 函数
const AVCodec* avcodec_find_decoder(enum AVCodecID id) {
    return ::avcodec_find_decoder(id);
}

const AVCodec* avcodec_find_decoder_by_name(const char* name) {
    return ::avcodec_find_decoder_by_name(name);
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

int avcodec_send_packet(AVCodecContext* avctx, const AVPacket* pkt) {
    return ::avcodec_send_packet(avctx, pkt);
}

int avcodec_receive_frame(AVCodecContext* avctx, AVFrame* frame) {
    return ::avcodec_receive_frame(avctx, frame);
}

int avcodec_flush_buffers(AVCodecContext* avctx) {
    return ::avcodec_flush_buffers(avctx);
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

int av_packet_ref(AVPacket* dst, const AVPacket* src) {
    return ::av_packet_ref(dst, src);
}

AVPacket* av_packet_clone(const AVPacket* src) {
    return ::av_packet_clone(src);
}

// avformat 函数
AVFormatContext* avformat_alloc_context() {
    return ::avformat_alloc_context();
}

void avformat_free_context(AVFormatContext* s) {
    ::avformat_free_context(s);
}

int avformat_open_input(AVFormatContext** ps, const char* url,
                        AVInputFormat* fmt, AVDictionary** options) {
    return ::avformat_open_input(ps, url, fmt, options);
}

void avformat_close_input(AVFormatContext** s) {
    ::avformat_close_input(s);
}

int avformat_find_stream_info(AVFormatContext* ic, AVDictionary** options) {
    return ::avformat_find_stream_info(ic, options);
}

int av_read_frame(AVFormatContext* s, AVPacket* pkt) {
    return ::av_read_frame(s, pkt);
}

int av_seek_frame(AVFormatContext* s, int stream_index, int64_t timestamp, int flags) {
    return ::av_seek_frame(s, stream_index, timestamp, flags);
}

AVStream* avformat_new_stream(AVFormatContext* s, const AVCodec* c) {
    return ::avformat_new_stream(s, c);
}

// swresample 函数
SwrContext* swr_alloc() {
    return ::swr_alloc();
}

int swr_init(SwrContext* s) {
    return ::swr_init(s);
}

void swr_free(SwrContext** s) {
    ::swr_free(s);
}

int swr_convert(SwrContext* s, uint8_t** out, int out_count,
                const uint8_t** in, int in_count) {
    return ::swr_convert(s, out, out_count, in, in_count);
}

int64_t swr_get_delay(SwrContext* s, int64_t base) {
    return ::swr_get_delay(s, base);
}

// swscale 函数
SwsContext* sws_getContext(int srcW, int srcH, enum AVPixelFormat srcFormat,
                           int dstW, int dstH, enum AVPixelFormat dstFormat,
                           int flags, SwsFilter* srcFilter,
                           SwsFilter* dstFilter, const double* param) {
    return ::sws_getContext(srcW, srcH, srcFormat, dstW, dstH, dstFormat,
                           flags, srcFilter, dstFilter, param);
}

SwsContext* sws_getCachedContext(SwsContext* context,
                                 int srcW, int srcH, enum AVPixelFormat srcFormat,
                                 int dstW, int dstH, enum AVPixelFormat dstFormat,
                                 int flags, SwsFilter* srcFilter,
                                 SwsFilter* dstFilter, const double* param) {
    return ::sws_getCachedContext(context, srcW, srcH, srcFormat, dstW, dstH, dstFormat,
                                 flags, srcFilter, dstFilter, param);
}

int sws_scale(SwsContext* c, const uint8_t* const srcSlice[],
              const int srcStride[], int srcSliceY, int srcSliceH,
              uint8_t* const dst[], const int dstStride[]) {
    return ::sws_scale(c, srcSlice, srcStride, srcSliceY, srcSliceH, dst, dstStride);
}

void sws_freeContext(SwsContext* swsContext) {
    ::sws_freeContext(swsContext);
}

// 字典操作
AVDictionary* av_dict_get(const AVDictionary* m, const char* key,
                          const AVDictionaryEntry* prev, int flags) {
    return ::av_dict_get(m, key, prev, flags);
}

int av_dict_set(AVDictionary** pm, const char* key, const char* value, int flags) {
    return ::av_dict_set(pm, key, value, flags);
}

void av_dict_free(AVDictionary** m) {
    ::av_dict_free(m);
}

// 时间基础转换
int64_t av_rescale_q(int64_t a, AVRational bq, AVRational cq) {
    return ::av_rescale_q(a, bq, cq);
}

int64_t av_rescale_q_rnd(int64_t a, AVRational bq, AVRational cq,
                         enum AVRounding rnd) {
    return ::av_rescale_q_rnd(a, bq, cq, rnd);
}

// 兼容性函数（FFmpeg 6.1.4 不再需要这些，但为兼容旧代码保留）
void avcodec_register_all() {
    LOGI("avcodec_register_all() is deprecated in FFmpeg 6.1.4");
}

void av_register_all() {
    LOGI("av_register_all() is deprecated in FFmpeg 6.1.4");
}

// 错误处理
const char* av_err2str(int errnum) {
    static char str[AV_ERROR_MAX_STRING_SIZE];
    memset(str, 0, sizeof(str));
    return av_make_error_string(str, AV_ERROR_MAX_STRING_SIZE, errnum);
}

// 内存管理辅助
void* av_memdup(const void* p, size_t size) {
    return ::av_memdup(p, size);
}

// 像素格式查询
const char* av_get_pix_fmt_name(enum AVPixelFormat pix_fmt) {
    return ::av_get_pix_fmt_name(pix_fmt);
}

// 获取默认通道布局
int64_t av_get_default_channel_layout(int nb_channels) {
    return ::av_get_default_channel_layout(nb_channels);
}

// 样本格式查询
const char* av_get_sample_fmt_name(enum AVSampleFormat sample_fmt) {
    return ::av_get_sample_fmt_name(sample_fmt);
}

} // extern "C" 结束
