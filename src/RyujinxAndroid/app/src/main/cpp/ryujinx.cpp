// ryujinx.cpp (完整修复版本，兼容FFmpeg 7.1.2)
#include "ryuijnx.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>

// 添加 FFmpeg 头文件
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/hwcontext.h>
#include <libavutil/opt.h>
#include <libavutil/imgutils.h>
// 移除不存在的 jni.h
}

// 全局变量定义 (在cpp文件中定义)
long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative;

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

// 硬件解码相关全局变量和结构体
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

// 日志标签 - 使用不同的标签避免冲突
#define LOG_TAG_NATIVE "RyujinxNative"
#define LOGI_NATIVE(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG_NATIVE, __VA_ARGS__)
#define LOGW_NATIVE(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG_NATIVE, __VA_ARGS__)
#define LOGE_NATIVE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG_NATIVE, __VA_ARGS__)
#define LOGD_NATIVE(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG_NATIVE, __VA_ARGS__)

extern "C"
{
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
        JNIEnv *env,
        jobject instance,
        jobject surface) {
    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    return nativeWindow == NULL ? -1 : (jlong) nativeWindow;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
        JNIEnv *env,
        jobject instance,
        jlong window) {
    auto nativeWindow = (ANativeWindow *) window;

    if (nativeWindow != NULL)
        ANativeWindow_release(nativeWindow);
}

long createSurface(long native_surface, long instance) {
    auto nativeWindow = (ANativeWindow *) native_surface;
    VkSurfaceKHR surface;
    auto vkInstance = (VkInstance) instance;
    auto fpCreateAndroidSurfaceKHR =
            reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(vkGetInstanceProcAddr(vkInstance,
                                                                                  "vkCreateAndroidSurfaceKHR"));
    if (!fpCreateAndroidSurfaceKHR)
        return -1;
    VkAndroidSurfaceCreateInfoKHR info = {VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR};
    info.window = nativeWindow;
    VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
    return (long) surface;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
        JNIEnv *env,
        jobject instance) {
    return (jlong) createSurface;
}

char *getStringPointer(
        JNIEnv *env,
        jstring jS) {
    const char *cparam = env->GetStringUTFChars(jS, 0);
    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    strcpy(s, cparam);
    env->ReleaseStringUTFChars(jS, cparam);

    return s;
}

jstring createString(
        JNIEnv *env,
        char *ch) {
    auto str = env->NewStringUTF(ch);

    return str;
}

jstring createStringFromStdString(
        JNIEnv *env,
        std::string s) {
    auto str = env->NewStringUTF(s.c_str());

    return str;
}
}
extern "C"
void setRenderingThread() {
    auto currentId = pthread_self();

    _renderingThreadIdNative = currentId;

    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

// JNI_OnLoad - 设置 JavaVM 给 FFmpeg
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    g_jvm = vm;
    
    // FFmpeg 7.1.2 不再需要显式设置 JavaVM
    // 硬件解码器会自动使用 Android 的 JNI 环境
    LOGI_NATIVE("FFmpeg JNI_OnLoad called");
    
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
    
    LOGI_NATIVE("FFmpeg JNI_OnUnload called");
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1)
        return;
    auto nativeWindow = (ANativeWindow *) native_window;

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;

    transform = transform >> 1;

    // transform is a valid VkSurfaceTransformFlagBitsKHR
    switch (transform) {
        case 0x1:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
        case 0x2:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90;
            break;
        case 0x4:
            nativeTransform = isInitialOrientationFlipped
                              ? ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY
                              : ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_180;
            break;
        case 0x8:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_270;
            break;
        case 0x10:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL;
            break;
        case 0x20:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x40:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
            break;
        case 0x80:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x100:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
    }

    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM,
                          static_cast<int32_t>(nativeTransform));
}

extern "C"
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    auto libPath = getStringPointer(env, native_lib_path);
    auto privateAppsPath = getStringPointer(env, private_apps_path);
    auto driverName = getStringPointer(env, driver_name);

    auto handle = adrenotools_open_libvulkan(
            RTLD_NOW,
            ADRENOTOOLS_DRIVER_CUSTOM,
            nullptr,
            libPath,
            privateAppsPath,
            driverName,
            nullptr,
            nullptr
    );

    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;

    return (jlong) handle;
}

extern "C"
void debug_break(int code) {
    if (code >= 3)
        int r = 0;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable);
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->maxSwapInterval;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->minSwapInterval;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->setSwapInterval(nativeWindow, swap_interval);
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    return createString(env, (char*)ptr);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

// =============== Oboe Audio JNI 接口 (修复版本) ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    bool result = RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI initOboeAudio: %dHz %dch -> %s", sample_rate, channel_count, result ? "success" : "failed");
    return result ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI shutdownOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Shutdown();
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jshortArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "JNI writeOboeAudio: invalid parameters");
        return JNI_FALSE;
    }

    jsize length = env->GetArrayLength(audio_data);
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    
    if (data) {
        bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        
        if (!success) {
            __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "JNI writeOboeAudio: write failed, %d frames", num_frames);
        }
        return success ? JNI_TRUE : JNI_FALSE;
    }
    
    __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "JNI writeOboeAudio: failed to get array elements");
    return JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI setOboeVolume: %.2f", volume);
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetVolume(volume);
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    bool initialized = RyujinxOboe::OboeAudioRenderer::GetInstance().IsInitialized();
    return initialized ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(JNIEnv *env, jobject thiz) {
    bool playing = RyujinxOboe::OboeAudioRenderer::GetInstance().IsPlaying();
    return playing ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    int32_t buffered = RyujinxOboe::OboeAudioRenderer::GetInstance().GetBufferedFrames();
    return static_cast<jint>(buffered);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI resetOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== 设备信息获取函数 ===============
extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char model[PROP_VALUE_MAX];
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX];
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

// =============== 硬件解码 JNI 接口 ===============

// 检查是否支持指定的硬件解码器类型
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isHardwareDecoderSupported(
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
        LOGW_NATIVE("Hardware device type %s not supported", type_str);
        
        // 输出支持的硬件类型
        AVHWDeviceType iter_type = AV_HWDEVICE_TYPE_NONE;
        LOGI_NATIVE("Available hardware device types:");
        while ((iter_type = av_hwdevice_iterate_types(iter_type)) != AV_HWDEVICE_TYPE_NONE) {
            const char* name = av_hwdevice_get_type_name(iter_type);
            if (name) {
                LOGI_NATIVE("  %s", name);
            }
        }
        
        return JNI_FALSE;
    }
    
    LOGI_NATIVE("Hardware device type %s is supported", type_str);
    return JNI_TRUE;
}

// 获取指定 codec 的硬件解码器名称
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getHardwareDecoderName(
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
Java_org_ryujinx_android_NativeHelpers_isHardwareDecoderAvailable(
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
    
    // 尝试查找硬件解码器 - 使用 const AVCodec*
    const AVCodec* codec = avcodec_find_decoder_by_name(hw_decoder_name);
    if (!codec) {
        LOGW_NATIVE("Hardware decoder %s not found", hw_decoder_name);
        return JNI_FALSE;
    }
    
    LOGI_NATIVE("Hardware decoder %s is available", hw_decoder_name);
    return JNI_TRUE;
}

// 获取硬件解码器的像素格式
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getHardwarePixelFormat(
    JNIEnv* env,
    jclass clazz,
    jstring decoder_name) {
    
    const char* decoder_str = env->GetStringUTFChars(decoder_name, nullptr);
    if (!decoder_str) {
        return -1;
    }
    
    const AVCodec* decoder = avcodec_find_decoder_by_name(decoder_str);
    env->ReleaseStringUTFChars(decoder_name, decoder_str);
    
    if (!decoder) {
        LOGE_NATIVE("Decoder %s not found", decoder_str);
        return -1;
    }
    
    // 查找硬件配置
    for (int i = 0; ; i++) {
        const AVCodecHWConfig* config = avcodec_get_hw_config(decoder, i);
        if (!config) {
            LOGW_NATIVE("No hardware config found for decoder %s at index %d", decoder_str, i);
            break;
        }
        
        if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) {
            LOGI_NATIVE("Found hardware config for %s: pix_fmt=%d, device_type=%s", 
                 decoder_str, config->pix_fmt, av_hwdevice_get_type_name(config->device_type));
            return config->pix_fmt;
        }
    }
    
    return -1;
}

// 获取支持的硬件解码器列表
JNIEXPORT jobjectArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getSupportedHardwareDecoders(
    JNIEnv* env,
    jclass clazz) {
    
    std::vector<std::string> available_decoders;
    
    for (const auto& pair : HARDWARE_DECODERS) {
        // 检查解码器是否可用 - 使用 const AVCodec*
        const AVCodec* codec = avcodec_find_decoder_by_name(pair.second.c_str());
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
Java_org_ryujinx_android_NativeHelpers_initHardwareDeviceContext(
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
        LOGE_NATIVE("Hardware device type not supported");
        return 0;
    }
    
    AVBufferRef* hw_device_ctx = nullptr;
    int err = av_hwdevice_ctx_create(&hw_device_ctx, type, nullptr, nullptr, 0);
    if (err < 0) {
        LOGE_NATIVE("Failed to create hardware device context: %s", av_err2str(err));
        return 0;
    }
    
    LOGI_NATIVE("Hardware device context created successfully");
    return reinterpret_cast<jlong>(hw_device_ctx);
}

// 释放硬件设备上下文
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_freeHardwareDeviceContext(
    JNIEnv* env,
    jclass clazz,
    jlong device_ctx_ptr) {
    
    if (device_ctx_ptr != 0) {
        AVBufferRef* hw_device_ctx = reinterpret_cast<AVBufferRef*>(device_ctx_ptr);
        av_buffer_unref(&hw_device_ctx);
        LOGI_NATIVE("Hardware device context freed");
    }
}

// 创建硬件解码器
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createHardwareDecoder(
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
    
    // 查找解码器 - 使用 const AVCodec*
    const AVCodec* decoder = avcodec_find_decoder_by_name(decoder_str);
    if (!decoder) {
        LOGE_NATIVE("Hardware decoder %s not found", decoder_str);
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 创建解码器上下文
    AVCodecContext* codec_ctx = avcodec_alloc_context3(decoder);
    if (!codec_ctx) {
        LOGE_NATIVE("Failed to allocate codec context for %s", decoder_str);
        env->ReleaseStringUTFChars(decoder_name, decoder_str);
        return 0;
    }
    
    // 设置硬件设备上下文
    codec_ctx->hw_device_ctx = av_buffer_ref(hw_device_ctx);
    if (!codec_ctx->hw_device_ctx) {
        LOGE_NATIVE("Failed to set hardware device context");
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
        if (config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) {
            // 直接使用第一个找到的硬件配置
            hw_pix_fmt = config->pix_fmt;
            break;
        }
    }
    
    if (hw_pix_fmt == AV_PIX_FMT_NONE) {
        LOGE_NATIVE("No suitable hardware config found for decoder %s", decoder_str);
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
        LOGE_NATIVE("Failed to allocate frames for hardware decoder");
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
        LOGE_NATIVE("Failed to open hardware decoder %s: %s", decoder_str, av_err2str(err));
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
    
    LOGI_NATIVE("Hardware decoder %s created successfully, context ID: %ld", decoder_str, context_id);
    env->ReleaseStringUTFChars(decoder_name, decoder_str);
    
    return context_id;
}

// 解码帧（硬件解码）
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_decodeFrame(
    JNIEnv* env,
    jclass clazz,
    jlong context_id,
    jbyteArray input_data,
    jint input_size,
    jbyteArray output_data) {
    
    if (context_id == 0) {
        LOGE_NATIVE("Invalid context ID");
        return -1;
    }
    
    auto it = g_decoder_contexts.find(context_id);
    if (it == g_decoder_contexts.end()) {
        LOGE_NATIVE("Decoder context not found for ID: %ld", context_id);
        return -1;
    }
    
    HardwareDecoderContext* ctx = it->second;
    if (!ctx->initialized || !ctx->codec_ctx) {
        LOGE_NATIVE("Decoder context not initialized");
        return -1;
    }
    
    // 创建 AVPacket
    AVPacket* packet = av_packet_alloc();
    if (!packet) {
        LOGE_NATIVE("Failed to allocate packet");
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
        LOGE_NATIVE("Error sending packet to decoder: %s", av_err2str(ret));
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
                LOGE_NATIVE("Error transferring data from GPU to CPU: %s", av_err2str(ret));
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
                            LOGW_NATIVE("Output buffer too small for plane %d", i);
                        }
                    }
                }
                
                env->ReleaseByteArrayElements(output_data, output_bytes, 0);
            }
        } else {
            // 直接使用软件帧
            LOGD_NATIVE("Frame is already in software format");
        }
    } else if (ret == AVERROR(EAGAIN)) {
        LOGD_NATIVE("Need more input data");
    } else if (ret == AVERROR_EOF) {
        LOGD_NATIVE("End of stream");
    } else {
        LOGE_NATIVE("Error receiving frame from decoder: %s", av_err2str(ret));
    }
    
    env->ReleaseByteArrayElements(input_data, input_bytes, JNI_ABORT);
    av_packet_free(&packet);
    av_frame_unref(ctx->hw_frame);
    av_frame_unref(ctx->sw_frame);
    
    return got_frame ? 0 : ret;
}

// 获取解码帧信息
JNIEXPORT jintArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getFrameInfo(
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
Java_org_ryujinx_android_NativeHelpers_flushDecoder(
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
        LOGD_NATIVE("Hardware decoder flushed");
    }
}

// 销毁硬件解码器
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyHardwareDecoder(
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
    LOGI_NATIVE("Hardware decoder destroyed, context ID: %ld", context_id);
}

// 获取硬件设备类型列表
JNIEXPORT jobjectArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getHardwareDeviceTypes(
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

// 获取 FFmpeg 版本信息
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getFFmpegVersion(JNIEnv* env, jclass clazz) {
    unsigned int version = avcodec_version();
    unsigned int major = (version >> 16) & 0xFF;
    unsigned int minor = (version >> 8) & 0xFF;
    unsigned int micro = version & 0xFF;
    
    char version_str[64];
    snprintf(version_str, sizeof(version_str), "FFmpeg %d.%d.%d", major, minor, micro);
    
    return env->NewStringUTF(version_str);
}

// 硬件解码器测试函数
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_testHardwareDecoder(JNIEnv *env, jobject thiz) {
    // 调用 FFmpegHardwareDecoder 的状态函数
    return env->NewStringUTF("Hardware decoder test function - use NativeHelpers class for full functionality");
}

// 获取硬件解码器支持状态
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isHardwareDecodingSupported(JNIEnv *env, jobject thiz) {
    // 检查 mediacodec 支持
    AVHWDeviceType type = av_hwdevice_find_type_by_name("mediacodec");
    return type != AV_HWDEVICE_TYPE_NONE ? JNI_TRUE : JNI_FALSE;
}

// =============== Oboe Audio C 接口 (for C# P/Invoke) ===============
extern "C"
bool initOboeAudio(int sample_rate, int channel_count) {
    bool result = RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C initOboeAudio: %dHz %dch -> %s", sample_rate, channel_count, result ? "success" : "failed");
    return result;
}

extern "C"
void shutdownOboeAudio() {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C shutdownOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Shutdown();
}

extern "C"
bool writeOboeAudio(const int16_t* data, int32_t num_frames) {
    if (!data || num_frames <= 0) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "C writeOboeAudio: invalid parameters");
        return false;
    }
    
    bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(data, num_frames);
    if (!success) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "C writeOboeAudio: write failed, %d frames", num_frames);
    }
    return success;
}

extern "C"
void setOboeVolume(float volume) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C setOboeVolume: %.2f", volume);
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetVolume(volume);
}

extern "C"
bool isOboeInitialized() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsInitialized();
}

extern "C"
bool isOboePlaying() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsPlaying();
}

extern "C"
int32_t getOboeBufferedFrames() {
    return static_cast<int32_t>(RyujinxOboe::OboeAudioRenderer::GetInstance().GetBufferedFrames());
}

extern "C"
void resetOboeAudio() {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C resetOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== 设备信息获取 C 接口 ===============
extern "C"
const char* GetAndroidDeviceModel() {
    static char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
    }
    return model;
}

extern "C"
const char* GetAndroidDeviceBrand() {
    static char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
    }
    return brand;
}