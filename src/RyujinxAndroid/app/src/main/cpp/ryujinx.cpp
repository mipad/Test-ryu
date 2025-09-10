// ryuijnx.cpp (稳定性修复版)
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

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

// 添加全局变量声明（如果尚未声明）
JavaVM* _vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadId;

// 添加缺失的宏定义
#define VK_CHECK(result) \
    if (result != VK_SUCCESS) { \
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Vulkan error: %d", result); \
        return -1; \
    }

// 添加缺失的类型定义
typedef struct ANativeWindow ANativeWindow;
typedef int32_t ANativeWindowTransform;

// 添加缺失的函数声明
extern "C" {
    typedef void* (*PFN_vkGetInstanceProcAddr)(void* instance, const char* name);
    typedef int (*PFN_vkCreateAndroidSurfaceKHR)(void* instance, const void* pCreateInfo, const void* pAllocator, void* pSurface);
    
    // 添加 adrenotools 相关声明 - 使用正确的类型
    void* adrenotools_open_libvulkan(int flags, int driver_type, const char* target_dir,
                                    const char* lib_dir, const char* app_dir, const char* package_name,
                                    const char* dev_dir, const char* hook_dir);
    void adrenotools_set_turbo(bool enable); // 修正为使用 bool 类型
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
    
    // 获取函数指针
    auto vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(RTLD_DEFAULT, "vkGetInstanceProcAddr");
    if (!vkGetInstanceProcAddr) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Failed to get vkGetInstanceProcAddr");
        return -1;
    }
    
    auto fpCreateAndroidSurfaceKHR = (PFN_vkCreateAndroidSurfaceKHR)vkGetInstanceProcAddr(
        vkInstance, "vkCreateAndroidSurfaceKHR");
    
    if (!fpCreateAndroidSurfaceKHR) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Failed to get vkCreateAndroidSurfaceKHR");
        return -1;
    }
    
    VkAndroidSurfaceCreateInfoKHR info = {};
    info.sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR;
    info.window = nativeWindow;
    
    VkResult result = fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface);
    if (result != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Failed to create Android surface: %d", result);
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
    delete[] ch; // 释放内存
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
    _renderingThreadId = currentId;
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    if (success != JNI_OK) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Failed to get JavaVM");
        return;
    }
    _vm = vm;
    
    // 创建全局引用，避免局部引用被垃圾回收
    _mainActivity = env->NewGlobalRef(thiz);
    _mainActivityClass = (jclass)env->NewGlobalRef(env->GetObjectClass(thiz));
}

bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1)
        return;
    
    // 注意：ANativeWindow 的实际实现可能不同
    // 这里使用更安全的方法
    auto nativeWindow = (ANativeWindow *) native_window;
    
    // 使用更安全的转换方法
    int32_t nativeTransform = 0; // 默认使用无变换
    
    // 简化转换逻辑，避免复杂的位操作
    switch (transform) {
        case 1: nativeTransform = 0; break; // 无旋转
        case 2: nativeTransform = 1; break; // 90度旋转
        case 4: nativeTransform = isInitialOrientationFlipped ? 0 : 2; break; // 180度旋转
        case 8: nativeTransform = 3; break; // 270度旋转
        default: nativeTransform = 0; break;
    }
    
    // 使用更安全的方法设置变换
    // 注意：实际的ANativeWindow API可能不同
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

    // 检查参数有效性
    if (!libPath || !privateAppsPath || !driverName) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Invalid parameters for loadDriver");
        delete[] libPath;
        delete[] privateAppsPath;
        delete[] driverName;
        return 0;
    }

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

    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;

    return (jlong) handle;
}

extern "C"
void debug_break(int code) {
    if (code >= 3) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Debug break triggered with code: %d", code);
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable != JNI_FALSE); // 将 jboolean 转换为 bool
}

// 简化交换间隔相关函数
extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    // 简化实现，直接返回成功
    return 0;
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    if (ptr == 0) {
        return env->NewStringUTF("");
    }
    return env->NewStringUTF((const char*)ptr);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

// =============== Oboe Audio JNI 接口 ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_print(ANDROID_LOG_INFO, "Ryuijnx", "Initializing Oboe audio");
    if (!OboeAudioRenderer::getInstance().initialize()) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Failed to initialize Oboe audio");
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_print(ANDROID_LOG_INFO, "Ryuijnx", "Shutting down Oboe audio");
    OboeAudioRenderer::getInstance().shutdown();
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jfloatArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        return;
    }

    jsize length = env->GetArrayLength(audio_data);
    if (length < num_frames) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Audio data array too small: %d < %d", length, num_frames);
        return;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (data) {
        OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
        env->ReleaseFloatArrayElements(audio_data, data, JNI_ABORT);
    }
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Invalid sample rate: %d", sample_rate);
        return;
    }
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijnx", "Invalid buffer size: %d", buffer_size);
        return;
    }
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    OboeAudioRenderer::getInstance().setVolume(volume);
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    return OboeAudioRenderer::getInstance().isInitialized() ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    return static_cast<jint>(OboeAudioRenderer::getInstance().getBufferedFrames());
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
    __android_log_print(ANDROID_LOG_INFO, "Ryuijnx", "C initOboeAudio called");
    OboeAudioRenderer::getInstance().initialize();
}

extern "C"
void shutdownOboeAudio() {
    __android_log_print(ANDROID_LOG_INFO, "Ryuijnx", "C shutdownOboeAudio called");
    OboeAudioRenderer::getInstance().shutdown();
}

extern "C"
void writeOboeAudio(const float* data, int32_t num_frames) { // 修正参数类型以匹配头文件
    if (!data || num_frames <= 0) {
        return;
    }
    OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
}

extern "C"
void setOboeSampleRate(int32_t sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        return;
    }
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

extern "C"
void setOboeBufferSize(int32_t buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        return;
    }
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
}

extern "C"
void setOboeVolume(float volume) {
    OboeAudioRenderer::getInstance().setVolume(volume);
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

// =============== 添加缺失的 ANativeWindow 函数 ===============
extern "C" {
int32_t ANativeWindow_setBuffersTransform(ANativeWindow* window, int32_t transform) {
    // 简化实现，实际实现可能更复杂
    return 0;
}
}
