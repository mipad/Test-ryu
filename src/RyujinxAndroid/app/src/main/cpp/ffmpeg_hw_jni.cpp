#include <jni.h>
#include <android/log.h>
#include <string>
#include <unordered_map>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/hwcontext.h>
#include <libavutil/opt.h>
#include <libavutil/imgutils.h>
}

#define LOG_TAG "FFmpegHWJNI"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

// 全局 JavaVM 引用
static JavaVM* g_jvm = nullptr;

// 硬件解码器类型映射
static std::unordered_map<std::string, std::string> HARDWARE_DECODERS = {
    {"h264", "h264_mediacodec"},
    {"hevc", "hevc_mediacodec"}, 
    {"vp8", "vp8_mediacodec"},
    {"vp9", "vp9_mediacodec"},
    {"av1", "av1_mediacodec"},
    {"mpeg4", "mpeg4_mediacodec"},
    {"mpeg2video", "mpeg2_mediacodec"}
};

// 硬件解码上下文结构
struct HardwareDecoderContext {
    AVCodecContext* codec_ctx;
    AVBufferRef* hw_device_ctx;
    AVFrame* hw_frame;
    AVFrame* sw_frame;
    enum AVPixelFormat hw_pix_fmt;
    bool initialized;
    
    HardwareDecoderContext() 
        : codec_ctx(nullptr)
        , hw_device_ctx(nullptr)
        , hw_frame(nullptr)
        , sw_frame(nullptr)
        , hw_pix_fmt(AV_PIX_FMT_NONE)
        , initialized(false) {}
};

// 全局解码器上下文映射
static std::unordered_map<jlong, HardwareDecoderContext*> g_decoder_contexts;
static jlong g_next_context_id = 1;

extern "C" {

// JNI_OnLoad - 设置 JavaVM 给 FFmpeg
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    g_jvm = vm;
    
    // 设置 JavaVM 给 FFmpeg
    av_jni_set_java_vm(vm, nullptr);
    
    LOGI("FFmpeg JNI_OnLoad called, JavaVM set for FFmpeg");
    
    // 初始化 FFmpeg
    avformat_network_init();
    
    return JNI_VERSION_1_6;
}

// JNI_OnUnload
JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    // 清理所有解码器上下文
    for (auto& pair : g_decoder_contexts) {
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
    g_decoder_contexts.clear();
    
    avformat_network_deinit();
    
    LOGI("FFmpeg JNI_OnUnload called");
}

// 检查是否支持指定的硬件解码器类型
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_isHardwareDecoderSupported(
    JNIEnv* env,
    jclass clazz,
    jstring decoder_type) {
    
    const char* type_str = env->GetStringUTFChars(decoder_type, nullptr);
    if (!type_str) {
        return JNI_FALSE;
    }
    
    // 查找硬件解码器类型
    AVHWDeviceType type = av_hwdevice_find_type_by_name(type_str);
    env->ReleaseStringUTFChars(decoder_type, type_str);
    
    if (type == AV_HWDEVICE_TYPE_NONE) {
        LOGW("Hardware device type %s not supported", type_str);
        
        // 输出支持的硬件类型
        AVHWDeviceType iter_type = AV_HWDEVICE_TYPE_NONE;
        LOGI("Available hardware device types:");
        while ((iter_type = av_hwdevice_iterate_types(iter_type)) != AV_HWDEVICE_TYPE_NONE) {
            const char* name = av_hwdevice_get_type_name(iter_type);
            if (name) {
                LOGI("  %s", name);
            }
        }
        
        return JNI_FALSE;
    }
    
    LOGI("Hardware device type %s is supported", type_str);
    return JNI_TRUE;
}

// 获取指定 codec 的硬件解码器名称
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_getHardwareDecoderName(
    JNIEnv* env,
    jclass clazz,
    jstring codec_name) {
    
    const char* codec_str = env->GetStringUTFChars(codec_name, nullptr);
    if (!codec_str) {
        return env->NewStringUTF("");
    }
    
    std::string codec_key = codec_str;
    env->ReleaseStringUTFChars(codec_name, codec_str);
    
    auto it = HARDWARE_DECODERS.find(codec_key);
    if (it != HARDWARE_DECODERS.end()) {
        return env->NewStringUTF(it->second.c_str());
    }
    
    return env->NewStringUTF("");
}

// 检查硬件解码器是否可用
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_isHardwareDecoderAvailable(
    JNIEnv* env,
    jclass clazz,
    jstring codec_name) {
    
    const char* codec_str = env->GetStringUTFChars(codec_name, nullptr);
    if (!codec_str) {
        return JNI_FALSE;
    }
    
    // 获取硬件解码器名称
    std::string codec_key = codec_str;
    env->ReleaseStringUTFChars(codec_name, codec_str);
    
    auto it = HARDWARE_DECODERS.find(codec_key);
    if (it == HARDWARE_DECODERS.end()) {
        return JNI_FALSE;
    }
    
    const char* hw_decoder_name = it->second.c_str();
    
    // 尝试查找硬件解码器
    AVCodec* codec = avcodec_find_decoder_by_name(hw_decoder_name);
    if (!codec) {
        LOGW("Hardware decoder %s not found", hw_decoder_name);
        return JNI_FALSE;
    }
    
    LOGI("Hardware decoder %s is available", hw_decoder_name);
    return JNI_TRUE;
}

// 获取硬件解码器的像素格式
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_getHardwarePixelFormat(
    JNIEnv* env,
    jclass clazz,
    jstring decoder_name) {
    
    const char* decoder_str = env->GetStringUTFChars(decoder_name, nullptr);
    if (!decoder_str) {
        return -1;
    }
    
    AVCodec* decoder = avcodec_find_decoder_by_name(decoder_str);
    env->ReleaseStringUTFChars(decoder_name, decoder_str);
    
    if (!decoder) {
        LOGE("Decoder %s not found", decoder_str);
        return -1;
    }
    
    // 查找硬件配置
    for (int i = 0; ; i++) {
        const AVCodecHWConfig* config = avcodec_get_hw_config(decoder, i);
        if (!config) {
            LOGW("No hardware config found for decoder %s at index %d", decoder_str, i);
            break;
        }
        
        if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) {
            LOGI("Found hardware config for %s: pix_fmt=%d, device_type=%s", 
                 decoder_str, config->pix_fmt, av_hwdevice_get_type_name(config->device_type));
            return config->pix_fmt;
        }
    }
    
    return -1;
}

// 获取支持的硬件解码器列表
JNIEXPORT jobjectArray JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_getSupportedHardwareDecoders(
    JNIEnv* env,
    jclass clazz) {
    
    std::vector<std::string> available_decoders;
    
    for (const auto& pair : HARDWARE_DECODERS) {
        // 检查解码器是否可用
        AVCodec* codec = avcodec_find_decoder_by_name(pair.second.c_str());
        if (codec) {
            std::string display_name = pair.first + " (" + pair.second + ")";
            available_decoders.push_back(display_name);
        }
    }
    
    // 创建字符串数组
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray result = env->NewObjectArray(available_decoders.size(), stringClass, nullptr);
    
    for (size_t i = 0; i < available_decoders.size(); i++) {
        env->SetObjectArrayElement(result, i, env->NewStringUTF(available_decoders[i].c_str()));
    }
    
    return result;
}

// 初始化硬件设备上下文
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_initHardwareDeviceContext(
    JNIEnv* env,
    jclass clazz,
    jstring device_type) {
    
    const char* type_str = env->GetStringUTFChars(device_type, nullptr);
    if (!type_str) {
        return 0;
    }
    
    AVHWDeviceType type = av_hwdevice_find_type_by_name(type_str);
    env->ReleaseStringUTFChars(device_type, type_str);
    
    if (type == AV_HWDEVICE_TYPE_NONE) {
        LOGE("Hardware device type not supported");
        return 0;
    }
    
    AVBufferRef* hw_device_ctx = nullptr;
    int err = av_hwdevice_ctx_create(&hw_device_ctx, type, nullptr, nullptr, 0);
    if (err < 0) {
        LOGE("Failed to create hardware device context: %s", av_err2str(err));
        return 0;
    }
    
    LOGI("Hardware device context created successfully");
    return reinterpret_cast<jlong>(hw_device_ctx);
}

// 释放硬件设备上下文
JNIEXPORT void JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_freeHardwareDeviceContext(
    JNIEnv* env,
    jclass clazz,
    jlong device_ctx_ptr) {
    
    if (device_ctx_ptr != 0) {
        AVBufferRef* hw_device_ctx = reinterpret_cast<AVBufferRef*>(device_ctx_ptr);
        av_buffer_unref(&hw_device_ctx);
        LOGI("Hardware device context freed");
    }
}

// 创建硬件解码器上下文
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_createHardwareDecoder(
    JNIEnv* env,
    jclass clazz,
    jstring decoder_name,
    jlong device_ctx_ptr) {
    
    const char* decoder_str = env->GetStringUTFChars(decoder_name, nullptr);
    if (!decoder_str) {
        return 0;
    }
    
    AVBufferRef* hw_device_ctx = reinterpret_cast<AVBufferRef*>(device_ctx_ptr);
    if (!hw_device_ctx) {
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 查找解码器
    AVCodec* decoder = avcodec_find_decoder_by_name(decoder_str);
    if (!decoder) {
        LOGE("Hardware decoder %s not found", decoder_str);
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 创建解码器上下文
    AVCodecContext* codec_ctx = avcodec_alloc_context3(decoder);
    if (!codec_ctx) {
        LOGE("Failed to allocate codec context for %s", decoder_str);
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 设置硬件设备上下文
    codec_ctx->hw_device_ctx = av_buffer_ref(hw_device_ctx);
    if (!codec_ctx->hw_device_ctx) {
        LOGE("Failed to set hardware device context");
        avcodec_free_context(&codec_ctx);
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 查找硬件像素格式
    enum AVPixelFormat hw_pix_fmt = AV_PIX_FMT_NONE;
    for (int i = 0; ; i++) {
        const AVCodecHWConfig* config = avcodec_get_hw_config(decoder, i);
        if (!config) {
            break;
        }
        if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX &&
            config->device_type == av_hwdevice_get_type(hw_device_ctx)) {
            hw_pix_fmt = config->pix_fmt;
            break;
        }
    }
    
    if (hw_pix_fmt == AV_PIX_FMT_NONE) {
        LOGE("No suitable hardware config found for decoder %s", decoder_str);
        avcodec_free_context(&codec_ctx);
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 创建硬件解码上下文
    HardwareDecoderContext* ctx = new HardwareDecoderContext();
    ctx->codec_ctx = codec_ctx;
    ctx->hw_device_ctx = hw_device_ctx;
    ctx->hw_pix_fmt = hw_pix_fmt;
    
    // 创建硬件帧
    ctx->hw_frame = av_frame_alloc();
    ctx->sw_frame = av_frame_alloc();
    
    if (!ctx->hw_frame || !ctx->sw_frame) {
        LOGE("Failed to allocate frames for hardware decoder");
        if (ctx->hw_frame) av_frame_free(&ctx->hw_frame);
        if (ctx->sw_frame) av_frame_free(&ctx->sw_frame);
        avcodec_free_context(&codec_ctx);
        delete ctx;
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 打开解码器
    int err = avcodec_open2(codec_ctx, decoder, nullptr);
    if (err < 0) {
        LOGE("Failed to open hardware decoder %s: %s", decoder_str, av_err2str(err));
        av_frame_free(&ctx->hw_frame);
        av_frame_free(&ctx->sw_frame);
        avcodec_free_context(&codec_ctx);
        delete ctx;
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    ctx->initialized = true;
    jlong context_id = g_next_context_id++;
    g_decoder_contexts[context_id] = ctx;
    
    LOGI("Hardware decoder %s created successfully, context ID: %ld", decoder_str, context_id);
    env->ReleaseStringUTFChars(decoder_name, decoder_str);
    
    return context_id;
}

// 解码帧（硬件解码）
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_decodeFrame(
    JNIEnv* env,
    jclass clazz,
    jlong context_id,
    jbyteArray input_data,
    jint input_size,
    jbyteArray output_data) {
    
    if (context_id == 0) {
        LOGE("Invalid context ID");
        return -1;
    }
    
    auto it = g_decoder_contexts.find(context_id);
    if (it == g_decoder_contexts.end()) {
        LOGE("Decoder context not found for ID: %ld", context_id);
        return -1;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (!ctx->initialized || !ctx->codec_ctx) {
        LOGE("Decoder context not initialized");
        return -1;
    }
    
    // 创建 AVPacket
    AVPacket* packet = av_packet_alloc();
    if (!packet) {
        LOGE("Failed to allocate packet");
        return -1;
    }
    
    // 设置输入数据
    jbyte* input_bytes = env->GetByteArrayElements(input_data, nullptr);
    packet->data = reinterpret_cast<uint8_t*>(input_bytes);
    packet->size = input_size;
    
    int ret = 0;
    int got_frame = 0;
    
    // 发送 packet 到解码器
    ret = avcodec_send_packet(ctx->codec_ctx, packet);
    if (ret < 0) {
        LOGE("Error sending packet to decoder: %s", av_err2str(ret));
        env->ReleaseByteArrayElements(input_data, input_bytes, JNI_ABORT);
        av_packet_free(&packet);
        return ret;
    }
    
    // 接收解码后的帧
    ret = avcodec_receive_frame(ctx->codec_ctx, ctx->hw_frame);
    if (ret == 0) {
        got_frame = 1;
        
        // 检查是否为硬件帧，需要转换
        if (ctx->hw_frame->format == ctx->hw_pix_fmt) {
            // 从 GPU 传输数据到 CPU
            ret = av_hwframe_transfer_data(ctx->sw_frame, ctx->hw_frame, 0);
            if (ret < 0) {
                LOGE("Error transferring data from GPU to CPU: %s", av_err2str(ret));
                got_frame = 0;
            } else {
                // 复制数据到输出缓冲区
                jbyte* output_bytes = env->GetByteArrayElements(output_data, nullptr);
                int data_size = 0;
                
                // 计算 YUV 数据大小
                for (int i = 0; i < AV_NUM_DATA_POINTERS; i++) {
                    if (ctx->sw_frame->data[i]) {
                        int plane_size = ctx->sw_frame->linesize[i] * ctx->sw_frame->height;
                        if (i > 0) {
                            plane_size = ctx->sw_frame->linesize[i] * (ctx->sw_frame->height / 2);
                        }
                        
                        if (data_size + plane_size <= env->GetArrayLength(output_data)) {
                            memcpy(output_bytes + data_size, ctx->sw_frame->data[i], plane_size);
                            data_size += plane_size;
                        } else {
                            LOGW("Output buffer too small for plane %d", i);
                        }
                    }
                }
                
                env->ReleaseByteArrayElements(output_data, output_bytes, 0);
            }
        } else {
            // 直接使用软件帧
            LOGD("Frame is already in software format");
        }
    } else if (ret == AVERROR(EAGAIN)) {
        LOGD("Need more input data");
    } else if (ret == AVERROR_EOF) {
        LOGD("End of stream");
    } else {
        LOGE("Error receiving frame from decoder: %s", av_err2str(ret));
    }
    
    env->ReleaseByteArrayElements(input_data, input_bytes, JNI_ABORT);
    av_packet_free(&packet);
    av_frame_unref(ctx->hw_frame);
    av_frame_unref(ctx->sw_frame);
    
    return got_frame ? 0 : ret;
}

// 获取解码帧信息
JNIEXPORT jintArray JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_getFrameInfo(
    JNIEnv* env,
    jclass clazz,
    jlong context_id) {
    
    if (context_id == 0) {
        return nullptr;
    }
    
    auto it = g_decoder_contexts.find(context_id);
    if (it == g_decoder_contexts.end()) {
        return nullptr;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (!ctx->initialized || !ctx->sw_frame) {
        return nullptr;
    }
    
    // 返回帧信息: [width, height, format, linesize0, linesize1, linesize2]
    jintArray result = env->NewIntArray(6);
    jint frame_info[6] = {
        ctx->sw_frame->width,
        ctx->sw_frame->height,
        ctx->sw_frame->format,
        ctx->sw_frame->linesize[0],
        ctx->sw_frame->linesize[1],
        ctx->sw_frame->linesize[2]
    };
    
    env->SetIntArrayRegion(result, 0, 6, frame_info);
    return result;
}

// 刷新解码器
JNIEXPORT void JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_flushDecoder(
    JNIEnv* env,
    jclass clazz,
    jlong context_id) {
    
    if (context_id == 0) {
        return;
    }
    
    auto it = g_decoder_contexts.find(context_id);
    if (it == g_decoder_contexts.end()) {
        return;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (ctx->initialized && ctx->codec_ctx) {
        avcodec_flush_buffers(ctx->codec_ctx);
        LOGD("Hardware decoder flushed");
    }
}

// 销毁硬件解码器
JNIEXPORT void JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_destroyHardwareDecoder(
    JNIEnv* env,
    jclass clazz,
    jlong context_id) {
    
    if (context_id == 0) {
        return;
    }
    
    auto it = g_decoder_contexts.find(context_id);
    if (it == g_decoder_contexts.end()) {
        return;
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
        // 注意：不要释放 hw_device_ctx，它由外部管理
        delete ctx;
    }
    
    g_decoder_contexts.erase(it);
    LOGI("Hardware decoder destroyed, context ID: %ld", context_id);
}

// 获取 FFmpeg 版本信息
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_getFFmpegVersion(
    JNIEnv* env,
    jclass clazz) {
    
    unsigned int version = avcodec_version();
    unsigned int major = (version >> 16) & 0xFF;
    unsigned int minor = (version >> 8) & 0xFF;
    unsigned int micro = version & 0xFF;
    
    char version_str[64];
    snprintf(version_str, sizeof(version_str), "FFmpeg %d.%d.%d", major, minor, micro);
    
    return env->NewStringUTF(version_str);
}

// 获取硬件设备类型列表
JNIEXPORT jobjectArray JNICALL
Java_org_ryujinx_android_FFmpegHardwareDecoder_getHardwareDeviceTypes(
    JNIEnv* env,
    jclass clazz) {
    
    std::vector<std::string> device_types;
    AVHWDeviceType type = AV_HWDEVICE_TYPE_NONE;
    
    while ((type = av_hwdevice_iterate_types(type)) != AV_HWDEVICE_TYPE_NONE) {
        const char* type_name = av_hwdevice_get_type_name(type);
        if (type_name) {
            device_types.push_back(type_name);
        }
    }
    
    jclass stringClass = env->FindClass("java/lang/String");
    jobjectArray result = env->NewObjectArray(device_types.size(), stringClass, nullptr);
    
    for (size_t i = 0; i < device_types.size(); i++) {
        env->SetObjectArrayElement(result, i, env->NewStringUTF(device_types[i].c_str()));
    }
    
    return result;
}

} // extern "C"