// ryuijnx.cpp (终极优化版：增强日志 + 健壮性，专为配合高质量Oboe音频后端)
#include "ryuijnx.h"
#include "pthread.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>
#include <cstdlib>
#include <cstring>
#include <stdexcept> // 添加异常处理头文件

// 使用统一日志标签
#define LOG_TAG "RyujinxJNI"
#define LOGD(...) 
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

// =============== Vulkan 相关 JNI 函数 ===============
extern "C" {
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
        JNIEnv *env,
        jobject instance,
        jobject surface) {
    if (!surface) {
        LOGE("getNativeWindow: surface is null");
        return -1;
    }

    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    if (nativeWindow == nullptr) {
        LOGE("getNativeWindow: ANativeWindow_fromSurface failed");
        return -1;
    }

    return (jlong) nativeWindow;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
        JNIEnv *env,
        jobject instance,
        jlong window) {
    auto nativeWindow = (ANativeWindow *) window;
    if (nativeWindow != nullptr) {
        ANativeWindow_release(nativeWindow);
    }
}

long createSurface(long native_surface, long instance) {
    auto nativeWindow = (ANativeWindow *) native_surface;
    if (nativeWindow == nullptr) {
        LOGE("createSurface: native_window is null");
        return -1;
    }

    VkSurfaceKHR surface = VK_NULL_HANDLE;
    auto vkInstance = (VkInstance) instance;
    if (vkInstance == VK_NULL_HANDLE) {
        LOGE("createSurface: vkInstance is null");
        return -1;
    }

    // 获取函数指针
    auto vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(RTLD_DEFAULT, "vkGetInstanceProcAddr");
    if (!vkGetInstanceProcAddr) {
        LOGE("createSurface: Failed to get vkGetInstanceProcAddr");
        return -1;
    }

    auto fpCreateAndroidSurfaceKHR = (PFN_vkCreateAndroidSurfaceKHR)vkGetInstanceProcAddr(
        vkInstance, "vkCreateAndroidSurfaceKHR");

    if (!fpCreateAndroidSurfaceKHR) {
        LOGE("createSurface: Failed to get vkCreateAndroidSurfaceKHR");
        return -1;
    }

    VkAndroidSurfaceCreateInfoKHR info = {};
    info.sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR;
    info.window = nativeWindow;

    VkResult result = fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface);
    if (result != VK_SUCCESS) {
        LOGE("createSurface: vkCreateAndroidSurfaceKHR failed with code %d", result);
        return -1;
    }

    return (long) surface;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
        JNIEnv *env,
        jobject instance) {
    return (jlong) createSurface;
}
}

// =============== 字符串工具函数 ===============
static char *getStringPointer(JNIEnv *env, jstring jS) {
    if (!jS) return nullptr;

    const char *cparam = env->GetStringUTFChars(jS, 0);
    if (!cparam) {
        LOGE("getStringPointer: GetStringUTFChars failed");
        return nullptr;
    }

    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    if (!s) {
        env->ReleaseStringUTFChars(jS, cparam);
        LOGE("getStringPointer: Memory allocation failed");
        return nullptr;
    }

    strcpy(s, cparam);
    env->ReleaseStringUTFChars(jS, cparam);

    return s;
}

extern "C" {
jstring createString(JNIEnv *env, char *ch) {
    if (!ch) {
        return env->NewStringUTF("");
    }

    jstring str = env->NewStringUTF(ch);
    delete[] ch;
    return str;
}

jstring createStringFromStdString(JNIEnv *env, std::string s) {
    return env->NewStringUTF(s.c_str());
}
}

// =============== 渲染线程与VM初始化 ===============
extern "C"
void setRenderingThread() {
    auto currentId = pthread_self();
    _renderingThreadId = currentId;
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    if (success != JNI_OK) {
        LOGE("initVm: Failed to get JavaVM, error=%d", success);
        return;
    }
    _vm = vm;

    _mainActivity = env->NewGlobalRef(thiz);
    if (!_mainActivity) {
        LOGE("initVm: Failed to create global ref for MainActivity");
        return;
    }

    jclass localClass = env->GetObjectClass(thiz);
    _mainActivityClass = (jclass)env->NewGlobalRef(localClass);
    if (!_mainActivityClass) {
        LOGE("initVm: Failed to create global ref for MainActivity class");
        env->DeleteGlobalRef(_mainActivity);
        _mainActivity = nullptr;
        return;
    }

    env->DeleteLocalRef(localClass);
}

// =============== 屏幕旋转与驱动加载 ===============
bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1) {
        return;
    }

    auto nativeWindow = (ANativeWindow *) native_window;
    int32_t nativeTransform = 0;

    switch (transform) {
        case 1: nativeTransform = 0; break; // 无旋转
        case 2: nativeTransform = 1; break; // 90度
        case 4: nativeTransform = isInitialOrientationFlipped ? 0 : 2; break; // 180度
        case 8: nativeTransform = 3; break; // 270度
        default:
            nativeTransform = 0;
            break;
    }

    ANativeWindow_setBuffersTransform(nativeWindow, nativeTransform);
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

    jlong result = 0;

    if (!libPath || !privateAppsPath || !driverName) {
        LOGE("loadDriver: Invalid parameters");
    } else {
        auto handle = adrenotools_open_libvulkan(
                RTLD_NOW,
                1, // ADRENOTOOLS_DRIVER_CUSTOM
                nullptr,
                libPath,
                privateAppsPath,
                driverName,
                nullptr,
                nullptr
        );

        if (!handle) {
            LOGE("loadDriver: adrenotools_open_libvulkan failed");
        } else {
            result = (jlong) handle;
        }
    }

    // 清理内存
    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;

    return result;
}

// =============== 调试与Turbo模式 ===============
extern "C"
void debug_break(int code) {
    if (code >= 3) {
        LOGE("debug_break: Triggered with code %d", code);
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    bool turboEnabled = (enable != JNI_FALSE);
    adrenotools_set_turbo(turboEnabled);
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    return 0;
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    if (ptr == 0) {
        return env->NewStringUTF("");
    }
    const char* str = (const char*)ptr;
    return env->NewStringUTF(str);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = (is_flipped != JNI_FALSE);
}

// =============== Oboe 音频 JNI 接口 (增强版) ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz) {
    try {
        if (!OboeAudioRenderer::getInstance().initialize()) {
            LOGE("initOboeAudio: FAILED to initialize Oboe audio");
        }
    } catch (const std::exception& e) {
        LOGE("initOboeAudio: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    try {
        OboeAudioRenderer::getInstance().shutdown();
    } catch (const std::exception& e) {
        LOGE("shutdownOboeAudio: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jfloatArray audio_data, jint num_frames, jint input_channels) {
    if (!audio_data || num_frames <= 0) {
        return;
    }

    jsize length = env->GetArrayLength(audio_data);
    if (length < num_frames * input_channels) {
        LOGE("writeOboeAudio: Array too small: length=%d, required=%d", length, num_frames * input_channels);
        return;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (!data) {
        LOGE("writeOboeAudio: GetFloatArrayElements failed");
        return;
    }

    try {
        OboeAudioRenderer::getInstance().writeAudio(data, num_frames, input_channels);
    } catch (const std::exception& e) {
        LOGE("writeOboeAudio: Exception: %s", e.what());
    }

    env->ReleaseFloatArrayElements(audio_data, data, JNI_ABORT);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    try {
        if (sample_rate < 8000 || sample_rate > 192000) {
            LOGE("setOboeSampleRate: Invalid sample rate: %d", sample_rate);
            return;
        }
        OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
    } catch (const std::exception& e) {
        LOGE("setOboeSampleRate: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    try {
        if (buffer_size < 64 || buffer_size > 8192) {
            LOGE("setOboeBufferSize: Invalid buffer size: %d", buffer_size);
            return;
        }
        OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
    } catch (const std::exception& e) {
        LOGE("setOboeBufferSize: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    try {
        OboeAudioRenderer::getInstance().setVolume(volume);
    } catch (const std::exception& e) {
        LOGE("setOboeVolume: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeNoiseShapingEnabled(JNIEnv *env, jobject thiz, jboolean enabled) {
    try {
        OboeAudioRenderer::getInstance().setNoiseShapingEnabled(enabled != JNI_FALSE);
    } catch (const std::exception& e) {
        LOGE("setOboeNoiseShapingEnabled: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeChannelCount(JNIEnv *env, jobject thiz, jint channel_count) {
    try {
        OboeAudioRenderer::getInstance().setChannelCount(channel_count);
    } catch (const std::exception& e) {
        LOGE("setOboeChannelCount: Exception: %s", e.what());
    }
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    try {
        bool initialized = OboeAudioRenderer::getInstance().isInitialized();
        return initialized ? JNI_TRUE : JNI_FALSE;
    } catch (const std::exception& e) {
        LOGE("isOboeInitialized: Exception: %s", e.what());
        return JNI_FALSE;
    }
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    try {
        return static_cast<jint>(OboeAudioRenderer::getInstance().getBufferedFrames());
    } catch (const std::exception& e) {
        LOGE("getOboeBufferedFrames: Exception: %s", e.what());
        return 0;
    }
}

// =============== 设备信息获取函数 ===============
extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char model[PROP_VALUE_MAX] = "Unknown";
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX] = "Unknown";
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

// =============== Oboe Audio C 接口 (for C# P/Invoke) ===============
extern "C"
void initOboeAudio() {
    try {
        OboeAudioRenderer::getInstance().initialize();
    } catch (const std::exception& e) {
        LOGE("initOboeAudio (C): Exception: %s", e.what());
    }
}

extern "C"
void shutdownOboeAudio() {
    try {
        OboeAudioRenderer::getInstance().shutdown();
    } catch (const std::exception& e) {
        LOGE("shutdownOboeAudio (C): Exception: %s", e.what());
    }
}

extern "C"
void writeOboeAudio(const float* data, int32_t num_frames, int32_t input_channels, int32_t output_channels) {
    if (!data || num_frames <= 0) {
        return;
    }
    
    try {
        // 设置输出通道数
        OboeAudioRenderer::getInstance().setChannelCount(output_channels);
        
        // 写入音频数据
        OboeAudioRenderer::getInstance().writeAudio(data, num_frames, input_channels);
    } catch (const std::exception& e) {
        LOGE("writeOboeAudio (C): Exception: %s", e.what());
    }
}

extern "C"
void setOboeSampleRate(int32_t sample_rate) {
    try {
        if (sample_rate < 8000 || sample_rate > 192000) {
            return;
        }
        OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
    } catch (const std::exception& e) {
        LOGE("setOboeSampleRate (C): Exception: %s", e.what());
    }
}

extern "C"
void setOboeBufferSize(int32_t buffer_size) {
    try {
        if (buffer_size < 64 || buffer_size > 8192) {
            return;
        }
        OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
    } catch (const std::exception& e) {
        LOGE("setOboeBufferSize (C): Exception: %s", e.what());
    }
}

extern "C"
void setOboeVolume(float volume) {
    try {
        OboeAudioRenderer::getInstance().setVolume(volume);
    } catch (const std::exception& e) {
        LOGE("setOboeVolume (C): Exception: %s", e.what());
    }
}

// =============== 噪声整形控制 C 接口 ===============
extern "C"
void setOboeNoiseShapingEnabled(bool enabled) {
    try {
        OboeAudioRenderer::getInstance().setNoiseShapingEnabled(enabled);
    } catch (const std::exception& e) {
        LOGE("setOboeNoiseShapingEnabled (C): Exception: %s", e.what());
    }
}

extern "C"
bool isOboeInitialized() {
    try {
        return OboeAudioRenderer::getInstance().isInitialized();
    } catch (const std::exception& e) {
        LOGE("isOboeInitialized (C): Exception: %s", e.what());
        return false;
    }
}

extern "C"
int32_t getOboeBufferedFrames() {
    try {
        return static_cast<int32_t>(OboeAudioRenderer::getInstance().getBufferedFrames());
    } catch (const std::exception& e) {
        LOGE("getOboeBufferedFrames (C): Exception: %s", e.what());
        return 0;
    }
}

// =============== 设备信息获取 C 接口 ===============
extern "C"
const char* GetAndroidDeviceModel() {
    static char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
        if (model[0] == '\0') {
            strcpy(model, "Unknown");
        }
    }
    return model;
}

extern "C"
const char* GetAndroidDeviceBrand() {
    static char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
        if (brand[0] == '\0') {
            strcpy(brand, "Unknown");
        }
    }
    return brand;
}

// =============== ANativeWindow 函数 (Stub) ===============
extern "C" {
int32_t ANativeWindow_setBuffersTransform(ANativeWindow* window, int32_t transform) {
    return 0;
}
}
