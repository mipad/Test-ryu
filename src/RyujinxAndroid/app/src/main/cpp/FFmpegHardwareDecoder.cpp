#include "FFmpegHardwareDecoder.h"
#include <android/log.h>

#define LOG_TAG_HW "FFmpegHwDecoder"
#define LOGI_HW(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG_HW, __VA_ARGS__)
#define LOGW_HW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG_HW, __VA_ARGS__)
#define LOGE_HW(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG_HW, __VA_ARGS__)
#define LOGD_HW(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG_HW, __VA_ARGS__)

// 硬件解码器类型映射
const std::unordered_map<std::string, std::string> FFmpegHardwareDecoder::HARDWARE_DECODERS = {
    {"h264", "h264_mediacodec"},
    {"hevc", "hevc_mediacodec"}, 
    {"vp8", "vp8_mediacodec"},
    {"vp9", "vp9_mediacodec"},
    {"av1", "av1_mediacodec"},
};

FFmpegHardwareDecoder& FFmpegHardwareDecoder::GetInstance() {
    static FFmpegHardwareDecoder instance;
    return instance;
}

FFmpegHardwareDecoder::FFmpegHardwareDecoder() 
    : nextContextId_(1), initialized_(false) {
}

FFmpegHardwareDecoder::~FFmpegHardwareDecoder() {
    Cleanup();
}

bool FFmpegHardwareDecoder::Initialize() {
    if (initialized_) {
        return true;
    }
    
    // 初始化 FFmpeg
    avformat_network_init();
    
    LOGI_HW("FFmpeg hardware decoder initialized");
    initialized_ = true;
    return true;
}

void FFmpegHardwareDecoder::Cleanup() {
    // 清理所有解码器上下文
    for (auto& pair : decoderContexts_) {
        HardwareDecoderContext* ctx = pair.second;
        if (ctx) {
            if (ctx->sw_frame) {
                av_frame_free(&ctx->sw_frame);
            }
            if (ctx->hw_frame) {
                av_frame_free(&ctx->hw_frame);
            }
            if (ctx->codec_ctx) {
                avcodec_free_context(&ctx->codec_ctx);
            }
            if (ctx->hw_device_ctx) {
                av_buffer_unref(&ctx->hw_device_ctx);
            }
            delete ctx;
        }
    }
    decoderContexts_.clear();
    
    avformat_network_deinit();
    
    LOGI_HW("FFmpeg hardware decoder cleaned up");
    initialized_ = false;
}

jlong FFmpegHardwareDecoder::CreateHardwareDecoderContext(JNIEnv* env, const char* codecName) {
    if (!initialized_) {
        LOGE_HW("Hardware decoder not initialized");
        return 0;
    }
    
    const char* decoderName = nullptr;
    auto it = HARDWARE_DECODERS.find(codecName);
    if (it != HARDWARE_DECODERS.end()) {
        decoderName = it->second.c_str();
    } else {
        // 如果找不到映射，直接使用传入的名称
        decoderName = codecName;
    }
    
    // 创建硬件设备上下文
    jlong deviceCtx = InitHardwareDeviceContext("mediacodec");
    if (deviceCtx == 0) {
        LOGE_HW("Failed to create hardware device context");
        return 0;
    }
    
    // 创建解码器上下文
    HardwareDecoderContext* ctx = new HardwareDecoderContext();
    jlong contextId = nextContextId_++;
    
    if (InitializeHardwareDecoder(contextId, decoderName, deviceCtx)) {
        decoderContexts_[contextId] = ctx;
        LOGI_HW("Hardware decoder context created: %ld for %s", contextId, decoderName);
        return contextId;
    } else {
        delete ctx;
        FreeHardwareDeviceContext(deviceCtx);
        LOGE_HW("Failed to initialize hardware decoder for %s", decoderName);
        return 0;
    }
}

bool FFmpegHardwareDecoder::InitializeHardwareDecoder(jlong contextId, const char* decoderName, jlong deviceCtxPtr) {
    AVBufferRef* hw_device_ctx = reinterpret_cast<AVBufferRef*>(deviceCtxPtr);
    if (!hw_device_ctx) {
        LOGE_HW("Invalid hardware device context");
        return false;
    }
    
    // 查找解码器
    const AVCodec* decoder = avcodec_find_decoder_by_name(decoderName);
    if (!decoder) {
        LOGE_HW("Hardware decoder %s not found", decoderName);
        return false;
    }
    
    // 创建解码器上下文
    AVCodecContext* codec_ctx = avcodec_alloc_context3(decoder);
    if (!codec_ctx) {
        LOGE_HW("Failed to allocate codec context for %s", decoderName);
        return false;
    }
    
    // 设置硬件设备上下文
    codec_ctx->hw_device_ctx = av_buffer_ref(hw_device_ctx);
    if (!codec_ctx->hw_device_ctx) {
        LOGE_HW("Failed to set hardware device context");
        avcodec_free_context(&codec_ctx);
        return false;
    }
    
    // 查找硬件像素格式
    enum AVPixelFormat hw_pix_fmt = AV_PIX_FMT_NONE;
    for (int i = 0; ; i++) {
        const AVCodecHWConfig* config = avcodec_get_hw_config(decoder, i);
        if (!config) {
            break;
        }
        if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) {
            // 直接使用第一个找到的硬件配置
            hw_pix_fmt = config->pix_fmt;
            break;
        }
    }
    
    if (hw_pix_fmt == AV_PIX_FMT_NONE) {
        LOGE_HW("No suitable hardware config found for decoder %s", decoderName);
        avcodec_free_context(&codec_ctx);
        return false;
    }
    
    // 获取上下文
    auto it = decoderContexts_.find(contextId);
    if (it == decoderContexts_.end()) {
        LOGE_HW("Decoder context not found for ID: %ld", contextId);
        avcodec_free_context(&codec_ctx);
        return false;
    }
    
    HardwareDecoderContext* ctx = it->second;
    
    // 创建硬件帧和软件帧
    ctx->hw_frame = av_frame_alloc();
    ctx->sw_frame = av_frame_alloc();
    
    if (!ctx->hw_frame || !ctx->sw_frame) {
        LOGE_HW("Failed to allocate frames for hardware decoder");
        if (ctx->hw_frame) av_frame_free(&ctx->hw_frame);
        if (ctx->sw_frame) av_frame_free(&ctx->sw_frame);
        avcodec_free_context(&codec_ctx);
        return false;
    }
    
    // 设置解码器上下文
    ctx->codec_ctx = codec_ctx;
    ctx->hw_device_ctx = hw_device_ctx;
    ctx->hw_pix_fmt = hw_pix_fmt;
    
    // 打开解码器
    int err = avcodec_open2(codec_ctx, decoder, nullptr);
    if (err < 0) {
        LOGE_HW("Failed to open hardware decoder %s: %s", decoderName, av_err2str(err));
        av_frame_free(&ctx->hw_frame);
        av_frame_free(&ctx->sw_frame);
        avcodec_free_context(&codec_ctx);
        return false;
    }
    
    ctx->initialized = true;
    LOGI_HW("Hardware decoder %s initialized successfully", decoderName);
    return true;
}

bool FFmpegHardwareDecoder::DestroyHardwareDecoderContext(jlong contextId) {
    auto it = decoderContexts_.find(contextId);
    if (it == decoderContexts_.end()) {
        LOGW_HW("Decoder context not found for ID: %ld", contextId);
        return false;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (ctx) {
        if (ctx->sw_frame) {
            av_frame_free(&ctx->sw_frame);
        }
        if (ctx->hw_frame) {
            av_frame_free(&ctx->hw_frame);
        }
        if (ctx->codec_ctx) {
            avcodec_free_context(&ctx->codec_ctx);
        }
        if (ctx->hw_device_ctx) {
            av_buffer_unref(&ctx->hw_device_ctx);
        }
        delete ctx;
    }
    
    decoderContexts_.erase(it);
    LOGI_HW("Hardware decoder destroyed, context ID: %ld", contextId);
    return true;
}

int FFmpegHardwareDecoder::DecodeVideoFrame(jlong contextId, jbyteArray inputData, jint inputSize,
                                           jintArray frameInfo, jobjectArray planeData) {
    if (contextId == 0) {
        LOGE_HW("Invalid context ID");
        return -1;
    }
    
    auto it = decoderContexts_.find(contextId);
    if (it == decoderContexts_.end()) {
        LOGE_HW("Decoder context not found for ID: %ld", contextId);
        return -1;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (!ctx->initialized || !ctx->codec_ctx) {
        LOGE_HW("Decoder context not initialized");
        return -1;
    }
    
    JNIEnv* env = nullptr;
    JavaVM* vm = nullptr;
    bool attached = false;
    
    // 获取 JNIEnv
    if (av_jni_get_java_vm(&vm) == 0 && vm) {
        jint result = vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
        if (result == JNI_EDETACHED) {
            if (vm->AttachCurrentThread(&env, nullptr) == 0) {
                attached = true;
            }
        }
    }
    
    if (!env) {
        LOGE_HW("Failed to get JNI environment");
        return -1;
    }
    
    // 创建 AVPacket
    AVPacket* packet = av_packet_alloc();
    if (!packet) {
        LOGE_HW("Failed to allocate packet");
        if (attached) {
            vm->DetachCurrentThread();
        }
        return -1;
    }
    
    // 设置输入数据
    jbyte* input_bytes = env->GetByteArrayElements(inputData, nullptr);
    packet->data = reinterpret_cast<uint8_t*>(input_bytes);
    packet->size = inputSize;
    
    int ret = 0;
    int got_frame = 0;
    
    // 发送 packet 到解码器
    ret = avcodec_send_packet(ctx->codec_ctx, packet);
    if (ret < 0) {
        LOGE_HW("Error sending packet to decoder: %s", av_err2str(ret));
        env->ReleaseByteArrayElements(inputData, input_bytes, JNI_ABORT);
        av_packet_free(&packet);
        if (attached) {
            vm->DetachCurrentThread();
        }
        return ret;
    }
    
    // 接收解码后的帧
    ret = avcodec_receive_frame(ctx->codec_ctx, ctx->hw_frame);
    if (ret == 0) {
        got_frame = 1;
        
        // 检查是否为硬件帧，需要转换
        if (ctx->hw_frame->format == ctx->hw_pix_fmt) {
            // 从 GPU 传输数据到 CPU
            ret = TransferHardwareFrameToSoftware(ctx);
            if (ret < 0) {
                LOGE_HW("Error transferring data from GPU to CPU: %s", av_err2str(ret));
                got_frame = 0;
            }
        } else {
            // 直接使用软件帧
            LOGD_HW("Frame is already in software format");
            av_frame_unref(ctx->sw_frame);
            if (av_frame_ref(ctx->sw_frame, ctx->hw_frame) < 0) {
                LOGE_HW("Failed to reference software frame");
                got_frame = 0;
            }
        }
        
        if (got_frame) {
            // 返回帧信息
            jint info[6] = {
                ctx->sw_frame->width,
                ctx->sw_frame->height,
                ctx->sw_frame->format,
                ctx->sw_frame->linesize[0],
                ctx->sw_frame->linesize[1],
                ctx->sw_frame->linesize[2]
            };
            env->SetIntArrayRegion(frameInfo, 0, 6, info);
            
            // 返回平面数据
            for (int i = 0; i < 3 && ctx->sw_frame->data[i]; i++) {
                int plane_height = (i == 0) ? ctx->sw_frame->height : ctx->sw_frame->height / 2;
                int plane_size = ctx->sw_frame->linesize[i] * plane_height;
                
                jbyteArray plane_array = (jbyteArray)env->GetObjectArrayElement(planeData, i);
                if (plane_array != nullptr) {
                    jsize array_length = env->GetArrayLength(plane_array);
                    if (array_length >= plane_size) {
                        jbyte* plane_bytes = env->GetByteArrayElements(plane_array, nullptr);
                        memcpy(plane_bytes, ctx->sw_frame->data[i], plane_size);
                        env->ReleaseByteArrayElements(plane_array, plane_bytes, 0);
                    } else {
                        LOGW_HW("Output buffer too small for plane %d: needed %d, got %d", 
                               i, plane_size, array_length);
                    }
                }
            }
        }
    } else if (ret == AVERROR(EAGAIN)) {
        LOGD_HW("Need more input data");
    } else if (ret == AVERROR_EOF) {
        LOGD_HW("End of stream");
    } else {
        LOGE_HW("Error receiving frame from decoder: %s", av_err2str(ret));
    }
    
    env->ReleaseByteArrayElements(inputData, input_bytes, JNI_ABORT);
    av_packet_free(&packet);
    av_frame_unref(ctx->hw_frame);
    av_frame_unref(ctx->sw_frame);
    
    if (attached) {
        vm->DetachCurrentThread();
    }
    
    return got_frame ? 0 : ret;
}

bool FFmpegHardwareDecoder::TransferHardwareFrameToSoftware(HardwareDecoderContext* ctx) {
    if (!ctx->hw_frame || !ctx->sw_frame) {
        return false;
    }
    
    // 从 GPU 传输数据到 CPU
    int ret = av_hwframe_transfer_data(ctx->sw_frame, ctx->hw_frame, 0);
    if (ret < 0) {
        LOGE_HW("Error transferring data from GPU to CPU: %s", av_err2str(ret));
        return false;
    }
    
    // 复制其他帧信息
    ctx->sw_frame->width = ctx->hw_frame->width;
    ctx->sw_frame->height = ctx->hw_frame->height;
    ctx->sw_frame->format = av_hwframe_transfer_get_formats(ctx->hw_device_ctx, 
                                                           AV_HWFRAME_TRANSFER_DIRECTION_FROM);
    
    return true;
}

// 以下工具函数的实现...
bool FFmpegHardwareDecoder::IsHardwareDecoderSupported(const char* decoderType) {
    AVHWDeviceType type = av_hwdevice_find_type_by_name(decoderType);
    return type != AV_HWDEVICE_TYPE_NONE;
}

const char* FFmpegHardwareDecoder::GetHardwareDecoderName(const char* codecName) {
    auto it = HARDWARE_DECODERS.find(codecName);
    if (it != HARDWARE_DECODERS.end()) {
        return it->second.c_str();
    }
    return "";
}

bool FFmpegHardwareDecoder::IsHardwareDecoderAvailable(const char* codecName) {
    const char* hw_decoder_name = GetHardwareDecoderName(codecName);
    if (strlen(hw_decoder_name) == 0) {
        return false;
    }
    
    const AVCodec* codec = avcodec_find_decoder_by_name(hw_decoder_name);
    return codec != nullptr;
}

int FFmpegHardwareDecoder::GetHardwarePixelFormat(const char* decoderName) {
    const AVCodec* decoder = avcodec_find_decoder_by_name(decoderName);
    if (!decoder) {
        return -1;
    }
    
    for (int i = 0; ; i++) {
        const AVCodecHWConfig* config = avcodec_get_hw_config(decoder, i);
        if (!config) {
            break;
        }
        
        if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) {
            return config->pix_fmt;
        }
    }
    
    return -1;
}

std::vector<std::string> FFmpegHardwareDecoder::GetSupportedHardwareDecoders() {
    std::vector<std::string> available_decoders;
    
    for (const auto& pair : HARDWARE_DECODERS) {
        const AVCodec* codec = avcodec_find_decoder_by_name(pair.second.c_str());
        if (codec) {
            std::string display_name = pair.first + " (" + pair.second + ")";
            available_decoders.push_back(display_name);
        }
    }
    
    return available_decoders;
}

std::vector<std::string> FFmpegHardwareDecoder::GetHardwareDeviceTypes() {
    std::vector<std::string> device_types;
    AVHWDeviceType type = AV_HWDEVICE_TYPE_NONE;
    
    while ((type = av_hwdevice_iterate_types(type)) != AV_HWDEVICE_TYPE_NONE) {
        const char* type_name = av_hwdevice_get_type_name(type);
        if (type_name) {
            device_types.push_back(type_name);
        }
    }
    
    return device_types;
}

jlong FFmpegHardwareDecoder::InitHardwareDeviceContext(const char* deviceType) {
    AVHWDeviceType type = av_hwdevice_find_type_by_name(deviceType);
    if (type == AV_HWDEVICE_TYPE_NONE) {
        LOGE_HW("Hardware device type %s not supported", deviceType);
        return 0;
    }
    
    AVBufferRef* hw_device_ctx = nullptr;
    int err = av_hwdevice_ctx_create(&hw_device_ctx, type, nullptr, nullptr, 0);
    if (err < 0) {
        LOGE_HW("Failed to create hardware device context: %s", av_err2str(err));
        return 0;
    }
    
    LOGI_HW("Hardware device context created successfully for %s", deviceType);
    return reinterpret_cast<jlong>(hw_device_ctx);
}

void FFmpegHardwareDecoder::FreeHardwareDeviceContext(jlong deviceCtxPtr) {
    if (deviceCtxPtr != 0) {
        AVBufferRef* hw_device_ctx = reinterpret_cast<AVBufferRef*>(deviceCtxPtr);
        av_buffer_unref(&hw_device_ctx);
        LOGI_HW("Hardware device context freed");
    }
}

void FFmpegHardwareDecoder::FlushDecoder(jlong contextId) {
    auto it = decoderContexts_.find(contextId);
    if (it == decoderContexts_.end()) {
        return;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (ctx->initialized && ctx->codec_ctx) {
        avcodec_flush_buffers(ctx->codec_ctx);
        LOGD_HW("Hardware decoder flushed");
    }
}

const char* FFmpegHardwareDecoder::GetFFmpegVersion() {
    static char version_str[64] = {0};
    if (version_str[0] == '\0') {
        unsigned int version = avcodec_version();
        unsigned int major = (version >> 16) & 0xFF;
        unsigned int minor = (version >> 8) & 0xFF;
        unsigned int micro = version & 0xFF;
        snprintf(version_str, sizeof(version_str), "FFmpeg %d.%d.%d", major, minor, micro);
    }
    return version_str;
}