// ryujinx.cpp (增强调试版本)
#include "ryuijnx.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>

// 全局变量定义
long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative;

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

// FFmpeg JNI 相关函数声明
extern "C" {
    // FFmpeg MediaCodec 需要的 JNI 函数
    void av_jni_set_java_vm(void *vm, void *log_ctx);
    int av_jni_get_java_vm(void **vm);
    
    // FFmpeg 错误处理函数
    char* av_err2str(int errnum);
}

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
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
    
    // 设置 FFmpeg 的 Java VM
    av_jni_set_java_vm(vm, nullptr);
    __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "JVM set for FFmpeg MediaCodec");
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

// =============== FFmpeg JNI 支持 ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setupFFmpegJNI(JNIEnv *env, jobject thiz) {
    // 确保 FFmpeg 可以使用 JNI
    void* vm = nullptr;
    int result = av_jni_get_java_vm(&vm);
    __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "av_jni_get_java_vm result: %d, vm: %p", result, vm);
    
    if (vm == nullptr) {
        // 如果 FFmpeg 没有获取到 JVM，重新设置
        JavaVM* jvm = nullptr;
        env->GetJavaVM(&jvm);
        if (jvm != nullptr) {
            av_jni_set_java_vm(jvm, nullptr);
            __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "FFmpeg JNI setup completed - JVM set manually");
            
            // 验证设置
            void* verify_vm = nullptr;
            int verify_result = av_jni_get_java_vm(&verify_vm);
            __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "JNI setup verification - result: %d, vm: %p", verify_result, verify_vm);
        } else {
            __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Failed to get JVM for FFmpeg");
        }
    } else {
        __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "FFmpeg JNI already setup - using existing JVM");
    }
}

// =============== FFmpeg 硬件解码器检测 ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isFFmpegHardwareDecoderAvailable(JNIEnv *env, jobject thiz, jstring codec_name) {
    const char* codec_name_str = env->GetStringUTFChars(codec_name, nullptr);
    if (!codec_name_str) {
        return JNI_FALSE;
    }
    
    // 这里需要链接 FFmpeg 库来检测解码器
    // 由于链接问题，我们暂时返回 true，让上层代码自己检测
    __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "Checking hardware decoder: %s", codec_name_str);
    
    env->ReleaseStringUTFChars(codec_name, codec_name_str);
    return JNI_TRUE;
}

// =============== Oboe Audio JNI 接口 ===============
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

// =============== Oboe Audio C 接口 ===============
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

// =============== FFmpeg C 接口 ===============
extern "C"
void setupFFmpegJNI() {
    // 确保 FFmpeg 可以使用 JNI
    void* vm = nullptr;
    int result = av_jni_get_java_vm(&vm);
    __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "C setupFFmpegJNI - av_jni_get_java_vm result: %d, vm: %p", result, vm);
    
    if (vm == nullptr) {
        // 如果 FFmpeg 没有获取到 JVM，使用全局的 _vm
        if (_vm != nullptr) {
            av_jni_set_java_vm(_vm, nullptr);
            __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "FFmpeg JNI setup completed from C");
            
            // 验证设置
            void* verify_vm = nullptr;
            int verify_result = av_jni_get_java_vm(&verify_vm);
            __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "C JNI setup verification - result: %d, vm: %p", verify_result, verify_vm);
        } else {
            __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "C setupFFmpegJNI failed: _vm is null");
        }
    } else {
        __android_log_print(ANDROID_LOG_INFO, "Ryujinx", "FFmpeg JNI already set from C, vm: %p", vm);
    }
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