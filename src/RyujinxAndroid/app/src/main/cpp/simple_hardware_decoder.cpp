// simple_hardware_decoder.cpp - 修复为C接口，与C# P/Invoke匹配
#include <memory>
#include <string>
#include <vector>
#include <android/log.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/avutil.h>
#include <libavutil/pixdesc.h>
#include <libavutil/hwcontext_mediacodec.h>
}

#define LOG_TAG "HardwareDecoder"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// 与C#结构体匹配的简化定义
typedef enum {
    SIMPLE_HW_CODEC_H264 = 0,
    SIMPLE_HW_CODEC_VP8 = 1,
    SIMPLE_HW_CODEC_VP9 = 2
} SimpleHWCodecType;

typedef struct {
    uint8_t* data[3];      // Y, U, V 平面
    int linesize[3];       // 行大小
    int width;
    int height;
    int format;           // 0=YUV420P, 1=NV12
    int key_frame;
    int64_t pts;
} SimpleHWFrame;

typedef struct {
    AVCodecContext* codec_ctx;
    AVBufferRef* hw_device_ctx;
    AVFrame* hw_frame;
    AVFrame* sw_frame;
    enum AVPixelFormat hw_pix_fmt;
    bool use_mediacodec;
    std::string last_error;
} SimpleContext;

// 获取支持的硬件设备类型
static AVHWDeviceType get_preferred_hw_device() {
    AVHWDeviceType type = AV_HWDEVICE_TYPE_MEDIACODEC;
    
    // 检查MediaCodec是否可用
    if (av_hwdevice_ctx_create(nullptr, type, nullptr, nullptr, 0) == 0) {
        LOGI("MediaCodec hardware decoder is available");
        return type;
    }
    
    LOGI("MediaCodec hardware decoder is NOT available");
    return AV_HWDEVICE_TYPE_NONE;
}

// 获取硬件像素格式回调
static enum AVPixelFormat get_hw_format(AVCodecContext* ctx, const enum AVPixelFormat* pix_fmts) {
    const enum AVPixelFormat* p;
    
    for (p = pix_fmts; *p != AV_PIX_FMT_NONE; p++) {
        if (*p == ctx->pix_fmt) {
            LOGI("Using hardware pixel format: %d", *p);
            return *p;
        }
    }
    
    LOGI("No hardware pixel format found, falling back to software");
    return AV_PIX_FMT_YUV420P;
}

// 创建解码器 - C接口
extern "C" void* hw_create(SimpleHWCodecType codec_type, int width, int height, bool use_hw) {
    SimpleContext* ctx = nullptr;
    
    try {
        ctx = new SimpleContext();
        memset(ctx, 0, sizeof(SimpleContext));
        ctx->last_error = "";
        
        // 根据codec_type选择解码器
        AVCodecID codec_id;
        const char* codec_name = nullptr;
        
        switch (codec_type) {
            case SIMPLE_HW_CODEC_H264:
                codec_id = AV_CODEC_ID_H264;
                codec_name = "h264_mediacodec";
                break;
            case SIMPLE_HW_CODEC_VP8:
                codec_id = AV_CODEC_ID_VP8;
                codec_name = "vp8_mediacodec";
                break;
            case SIMPLE_HW_CODEC_VP9:
                codec_id = AV_CODEC_ID_VP9;
                codec_name = "vp9_mediacodec";
                break;
            default:
                ctx->last_error = "Unsupported codec type";
                LOGE("%s", ctx->last_error.c_str());
                delete ctx;
                return nullptr;
        }
        
        LOGI("Creating hardware decoder for %s (%dx%d), use_hw=%d", 
              codec_name, width, height, use_hw);
        
        // 查找解码器
        const AVCodec* codec = nullptr;
        ctx->use_mediacodec = use_hw;
        
        if (use_hw && codec_name) {
            codec = avcodec_find_decoder_by_name(codec_name);
            if (codec) {
                LOGI("Found hardware decoder: %s", codec_name);
            }
        }
        
        // 回退到软件解码器
        if (!codec) {
            codec = avcodec_find_decoder(codec_id);
            ctx->use_mediacodec = false;
            if (codec) {
                LOGI("Using software decoder: %s", codec->name);
            }
        }
        
        if (!codec) {
            ctx->last_error = "Codec not found";
            LOGE("%s", ctx->last_error.c_str());
            delete ctx;
            return nullptr;
        }
        
        // 创建解码器上下文
        ctx->codec_ctx = avcodec_alloc_context3(codec);
        if (!ctx->codec_ctx) {
            ctx->last_error = "Failed to allocate codec context";
            LOGE("%s", ctx->last_error.c_str());
            delete ctx;
            return nullptr;
        }
        
        // 配置解码器
        ctx->codec_ctx->width = width;
        ctx->codec_ctx->height = height;
        ctx->codec_ctx->thread_count = 0; // 自动选择线程数
        
        // 尝试硬件解码
        if (ctx->use_mediacodec) {
            AVHWDeviceType hw_type = get_preferred_hw_device();
            if (hw_type != AV_HWDEVICE_TYPE_NONE) {
                // 获取硬件配置
                for (int i = 0; ; i++) {
                    const AVCodecHWConfig* config = avcodec_get_hw_config(codec, i);
                    if (!config) break;
                    
                    if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX &&
                        config->device_type == hw_type) {
                        ctx->hw_pix_fmt = config->pix_fmt;
                        
                        // 创建硬件设备上下文
                        if (av_hwdevice_ctx_create(&ctx->hw_device_ctx, hw_type,
                                                    nullptr, nullptr, 0) < 0) {
                            ctx->last_error = "Failed to create hardware device context";
                            LOGE("%s", ctx->last_error.c_str());
                            break;
                        }
                        
                        ctx->codec_ctx->hw_device_ctx = av_buffer_ref(ctx->hw_device_ctx);
                        ctx->codec_ctx->get_format = get_hw_format;
                        ctx->codec_ctx->pix_fmt = ctx->hw_pix_fmt;
                        LOGI("Hardware acceleration enabled with pixel format: %d", ctx->hw_pix_fmt);
                        break;
                    }
                }
            }
        }
        
        // 打开解码器
        if (avcodec_open2(ctx->codec_ctx, codec, nullptr) < 0) {
            ctx->last_error = "Failed to open codec";
            LOGE("%s", ctx->last_error.c_str());
            if (ctx->hw_device_ctx) {
                av_buffer_unref(&ctx->hw_device_ctx);
            }
            avcodec_free_context(&ctx->codec_ctx);
            delete ctx;
            return nullptr;
        }
        
        // 分配帧
        ctx->hw_frame = av_frame_alloc();
        ctx->sw_frame = av_frame_alloc();
        
        if (!ctx->hw_frame || !ctx->sw_frame) {
            if (ctx->hw_frame) av_frame_free(&ctx->hw_frame);
            if (ctx->sw_frame) av_frame_free(&ctx->sw_frame);
            if (ctx->hw_device_ctx) av_buffer_unref(&ctx->hw_device_ctx);
            avcodec_free_context(&ctx->codec_ctx);
            delete ctx;
            ctx->last_error = "Failed to allocate frames";
            LOGE("%s", ctx->last_error.c_str());
            return nullptr;
        }
        
        LOGI("Hardware decoder created successfully");
        return ctx;
        
    } catch (const std::exception& e) {
        if (ctx) {
            ctx->last_error = e.what();
            LOGE("Exception in hw_create: %s", e.what());
            delete ctx;
        }
        return nullptr;
    } catch (...) {
        if (ctx) {
            ctx->last_error = "Unknown exception";
            LOGE("Unknown exception in hw_create");
            delete ctx;
        }
        return nullptr;
    }
}

// 解码帧 - C接口
extern "C" int hw_decode(void* handle, uint8_t* data, int size, SimpleHWFrame* out_frame) {
    if (!handle || !out_frame || !data || size <= 0) {
        LOGE("Invalid parameters in hw_decode");
        return -1;
    }
    
    SimpleContext* ctx = reinterpret_cast<SimpleContext*>(handle);
    
    // 创建AVPacket
    AVPacket* packet = av_packet_alloc();
    if (!packet) {
        LOGE("Failed to allocate AVPacket");
        return -1;
    }
    
    packet->data = data;
    packet->size = size;
    
    // 发送包
    int ret = avcodec_send_packet(ctx->codec_ctx, packet);
    av_packet_free(&packet);
    
    if (ret < 0) {
        ctx->last_error = "Failed to send packet: " + std::to_string(ret);
        LOGE("%s", ctx->last_error.c_str());
        return ret;
    }
    
    // 接收帧
    ret = avcodec_receive_frame(ctx->codec_ctx, ctx->hw_frame);
    if (ret < 0) {
        if (ret != AVERROR(EAGAIN) && ret != AVERROR_EOF) {
            ctx->last_error = "Failed to receive frame: " + std::to_string(ret);
            LOGE("%s", ctx->last_error.c_str());
        }
        return ret;
    }
    
    // 处理硬件帧（如果需要传输到系统内存）
    AVFrame* frame = ctx->hw_frame;
    if (ctx->hw_frame->hw_frames_ctx) {
        ret = av_hwframe_transfer_data(ctx->sw_frame, ctx->hw_frame, 0);
        if (ret < 0) {
            ctx->last_error = "Failed to transfer hardware frame: " + std::to_string(ret);
            LOGE("%s", ctx->last_error.c_str());
            return ret;
        }
        frame = ctx->sw_frame;
        LOGI("Hardware frame transferred to system memory");
    }
    
    // 填充输出结构
    out_frame->width = frame->width;
    out_frame->height = frame->height;
    out_frame->format = (frame->format == AV_PIX_FMT_NV12) ? 1 : 0;
    out_frame->key_frame = (frame->key_frame == 1);
    out_frame->pts = frame->pts;
    
    // 复制数据指针（注意：这些指针只在帧有效期内有效）
    for (int i = 0; i < 3; i++) {
        out_frame->data[i] = frame->data[i];
        out_frame->linesize[i] = frame->linesize[i];
    }
    
    // 如果只有两个平面（NV12），第三个平面设为null
    if (frame->format == AV_PIX_FMT_NV12) {
        out_frame->data[2] = nullptr;
        out_frame->linesize[2] = 0;
    }
    
    LOGI("Frame decoded: %dx%d, format=%d, key_frame=%d", 
          out_frame->width, out_frame->height, out_frame->format, out_frame->key_frame);
    
    return 0;
}

// 销毁解码器 - C接口
extern "C" void hw_destroy(void* handle) {
    if (!handle) return;
    
    SimpleContext* ctx = reinterpret_cast<SimpleContext*>(handle);
    
    LOGI("Destroying hardware decoder");
    
    if (ctx->codec_ctx) {
        avcodec_free_context(&ctx->codec_ctx);
    }
    if (ctx->hw_device_ctx) {
        av_buffer_unref(&ctx->hw_device_ctx);
    }
    if (ctx->hw_frame) {
        av_frame_free(&ctx->hw_frame);
    }
    if (ctx->sw_frame) {
        av_frame_free(&ctx->sw_frame);
    }
    
    delete ctx;
}

// 检查硬件解码器是否可用 - C接口
extern "C" bool hw_is_available() {
    // 检查是否有可用的MediaCodec解码器
    const char* hw_decoders[] = {
        "h264_mediacodec",
        "vp8_mediacodec",
        "vp9_mediacodec",
        nullptr
    };
    
    for (int i = 0; hw_decoders[i]; i++) {
        if (avcodec_find_decoder_by_name(hw_decoders[i])) {
            LOGI("Hardware decoder available: %s", hw_decoders[i]);
            return true;
        }
    }
    
    LOGI("No hardware decoder available");
    return false;
}

// 获取最后的错误信息 - C接口
extern "C" const char* hw_get_last_error(void* handle) {
    if (!handle) {
        static std::string no_handle_error = "No handle provided";
        return no_handle_error.c_str();
    }
    
    SimpleContext* ctx = reinterpret_cast<SimpleContext*>(handle);
    return ctx->last_error.c_str();
}
