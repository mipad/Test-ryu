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

// 使用统一日志标签
#define LOG_TAG "RyujinxJNI"
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)
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

    LOGI("getNativeWindow: Success, ptr=%p", nativeWindow);
    return (jlong) nativeWindow;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
        JNIEnv *env,
        jobject instance,
        jlong window) {
    auto nativeWindow = (ANativeWindow *) window;
    if (nativeWindow != nullptr) {
        LOGI("releaseNativeWindow: Releasing window ptr=%p", nativeWindow);
        ANativeWindow_release(nativeWindow);
    } else {
        LOGW("releaseNativeWindow: Window is null");
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

    LOGI("createSurface: Success, surface=%p", surface);
    return (long) surface;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
        JNIEnv *env,
        jobject instance) {
    LOGI("getCreateSurfacePtr: Returning function pointer");
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
        LOGE("createString: Input char* is null");
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
    LOGI("setRenderingThread: Rendering thread set to %lu", (unsigned long)currentId);
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
    LOGI("initVm: JavaVM, MainActivity, and Class refs initialized successfully");
}

// =============== 屏幕旋转与驱动加载 ===============
bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1) {
        LOGW("setCurrentTransform: Invalid native_window");
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
            LOGW("setCurrentTransform: Unknown transform value %d", transform);
            nativeTransform = 0;
            break;
    }

    int result = ANativeWindow_setBuffersTransform(nativeWindow, nativeTransform);
    if (result != 0) {
        LOGW("setCurrentTransform: ANativeWindow_setBuffersTransform returned %d", result);
    } else {
        LOGD("setCurrentTransform: Set transform to %d", nativeTransform);
    }
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

    if (!libPath || !privateAppsPath || !driverName) {
        LOGE("loadDriver: Invalid parameters");
        goto cleanup;
    }

    LOGI("loadDriver: Loading Vulkan driver: %s, %s, %s", libPath, privateAppsPath, driverName);

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
        LOGI("loadDriver: Driver loaded successfully, handle=%p", handle);
    }

cleanup:
    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;

    return (jlong) handle;
}

// =============== 调试与Turbo模式 ===============
extern "C"
void debug_break(int code) {
    if (code >= 3) {
        LOGE("debug_break: Triggered with code %d", code);
    } else {
        LOGD("debug_break: Code %d", code);
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    bool turboEnabled = (enable != JNI_FALSE);
    adrenotools_set_turbo(turboEnabled);
    LOGI("setTurboMode: Turbo mode %s", turboEnabled ? "ENABLED" : "DISABLED");
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    // 此函数通常由Vulkan Swapchain控制，此处简化
    LOGD("setSwapInterval: swap_interval=%d (ignored)", swap_interval);
    return 0;
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    if (ptr == 0) {
        LOGW("getStringJava: ptr is null");
        return env->NewStringUTF("");
    }
    const char* str = (const char*)ptr;
    LOGD("getStringJava: Returning string from ptr=%p", (void*)ptr);
    return env->NewStringUTF(str);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = (is_flipped != JNI_FALSE);
    LOGI("setIsInitialOrientationFlipped: Set to %s", isInitialOrientationFlipped ? "true" : "false");
}

// =============== Oboe 音频 JNI 接口 (增强版) ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz) {
    LOGI("initOboeAudio: Initializing Oboe audio renderer...");
    if (!OboeAudioRenderer::getInstance().initialize()) {
        LOGE("initOboeAudio: FAILED to initialize Oboe audio");
    } else {
        LOGI("initOboeAudio: Oboe audio initialized successfully");
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    LOGI("shutdownOboeAudio: Shutting down Oboe audio renderer...");
    OboeAudioRenderer::getInstance().shutdown();
    LOGI("shutdownOboeAudio: Oboe audio shutdown complete");
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jfloatArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        LOGW("writeOboeAudio: Invalid parameters, audio_data=%p, num_frames=%d", audio_data, num_frames);
        return;
    }

    jsize length = env->GetArrayLength(audio_data);
    if (length < num_frames) {
        LOGE("writeOboeAudio: Array too small: length=%d, required=%d", length, num_frames);
        return;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (!data) {
        LOGE("writeOboeAudio: GetFloatArrayElements failed");
        return;
    }

    // ✅ 关键：记录写入帧数，便于与C++端日志关联
    static int totalFramesWritten = 0;
    totalFramesWritten += num_frames;
    if (totalFramesWritten % 1000 == 0) {
        LOGD("writeOboeAudio: Total frames written: %d", totalFramesWritten);
    }

    OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
    env->ReleaseFloatArrayElements(audio_data, data, JNI_ABORT);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        LOGE("setOboeSampleRate: Invalid sample rate: %d", sample_rate);
        return;
    }
    LOGI("setOboeSampleRate: Setting to %d Hz", sample_rate);
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        LOGE("setOboeBufferSize: Invalid buffer size: %d", buffer_size);
        return;
    }
    LOGI("setOboeBufferSize: Setting to %d frames", buffer_size);
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    LOGD("setOboeVolume: Setting to %.2f", volume);
    OboeAudioRenderer::getInstance().setVolume(volume);
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    bool initialized = OboeAudioRenderer::getInstance().isInitialized();
    LOGD("isOboeInitialized: %s", initialized ? "true" : "false");
    return initialized ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    int frames = static_cast<jint>(OboeAudioRenderer::getInstance().getBufferedFrames());
    // ✅ 注释掉频繁日志，避免刷屏
    // LOGD("getOboeBufferedFrames: %d frames", frames);
    return frames;
}

// =============== 设备信息获取函数 ===============
extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char model[PROP_VALUE_MAX] = "Unknown";
    __system_property_get("ro.product.model", model);
    LOGD("getAndroidDeviceModel: %s", model);
    return env->NewStringUTF(model);
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX] = "Unknown";
    __system_property_get("ro.product.brand", brand);
    LOGD("getAndroidDeviceBrand: %s", brand);
    return env->NewStringUTF(brand);
}

// =============== Oboe Audio C 接口 (for C# P/Invoke) ===============
extern "C"
void initOboeAudio() {
    LOGI("C initOboeAudio called");
    OboeAudioRenderer::getInstance().initialize();
}

extern "C"
void shutdownOboeAudio() {
    LOGI("C shutdownOboeAudio called");
    OboeAudioRenderer::getInstance().shutdown();
}

extern "C"
void writeOboeAudio(const float* data, int32_t num_frames) {
    if (!data || num_frames <= 0) {
        LOGW("C writeOboeAudio: Invalid parameters");
        return;
    }
    OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
}

extern "C"
void setOboeSampleRate(int32_t sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        LOGE("C setOboeSampleRate: Invalid sample rate: %d", sample_rate);
        return;
    }
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

extern "C"
void setOboeBufferSize(int32_t buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        LOGE("C setOboeBufferSize: Invalid buffer size: %d", buffer_size);
        return;
    }
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
}

extern "C"
void setOboeVolume(float volume) {
    OboeAudioRenderer::getInstance().setVolume(volume);
}

// =============== 噪声整形控制 C 接口 ===============
extern "C"
void setOboeNoiseShapingEnabled(bool enabled) {
    LOGI("setOboeNoiseShapingEnabled: %s", enabled ? "true" : "false");
    OboeAudioRenderer::getInstance().setNoiseShapingEnabled(enabled);
}

extern "C"
bool isOboeInitialized() {
    return OboeAudioRenderer::getInstance().isInitialized();
}

extern "C"
int32_t getOboeBufferedFrames() {
    return static_cast<int32_t>(OboeAudioRenderer::getInstance().getBufferedFrames());
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
    // 实际实现由系统提供，此处仅为避免链接错误
    return 0;
}
}
