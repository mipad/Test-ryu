// Write C++ code here.
//
// Do not forget to dynamically load the C++ library into your application.
//
// For instance,
//
// In MainActivity.java:
//    static {
//       System.loadLibrary("ryuijnx");
//    }
//
// Or, in MainActivity.kt:
//    companion object {
//      init {
//         System.loadLibrary("ryuijnx")
//      }
//    }

#include "ryuijnx.h"
#include "pthread.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>

// Global variables
JavaVM* _vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadId;
std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;
bool isInitialOrientationFlipped = true;

// Vulkan helper macro
#define VK_CHECK(result) \
    if (result != VK_SUCCESS) { \
        __android_log_print(ANDROID_LOG_ERROR, "Ryuijinx", "Vulkan error: %d", result); \
        return -1; \
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
    _renderingThreadId = currentId;
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = env->NewGlobalRef(thiz);
    _mainActivityClass = (jclass)env->NewGlobalRef(env->GetObjectClass(thiz));
}

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

// =============== Oboe Audio JNI 接口 (双音频流共享模式) ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count) ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().Shutdown();
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jshortArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        return JNI_FALSE;
    }

    jsize length = env->GetArrayLength(audio_data);
    if (length < num_frames * 2) { // 假设2声道
        return JNI_FALSE;
    }

    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (data) {
        bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    
    return JNI_FALSE;
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudioToStream(JNIEnv *env, jobject thiz, jfloatArray audio_data, jint num_frames, jint stream_id) {
    if (!audio_data || num_frames <= 0) {
        return JNI_FALSE;
    }

    jsize length = env->GetArrayLength(audio_data);
    if (length < num_frames) {
        return JNI_FALSE;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (data) {
        bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudioToStream(data, num_frames, stream_id);
        env->ReleaseFloatArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    
    return JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        return;
    }
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetSampleRate(sample_rate);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        return;
    }
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetBufferSize(buffer_size);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetVolume(volume);
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(JNIEnv *env, jobject thiz) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    return static_cast<jint>(RyujinxOboe::OboeAudioRenderer::GetInstance().GetBufferedFrames());
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(JNIEnv *env, jobject thiz) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== 多流管理 JNI 接口 ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_createAdditionalOboeStream(JNIEnv *env, jobject thiz) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().CreateAdditionalStream() ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_switchToOboeStream(JNIEnv *env, jobject thiz, jint stream_id) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().SwitchToStream(stream_id) ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getCurrentOboeStreamId(JNIEnv *env, jobject thiz) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().GetCurrentStreamId();
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeStreamCount(JNIEnv *env, jobject thiz) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().GetStreamCount();
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

// =============== Oboe Audio C 接口 (for C# P/Invoke) ===============
extern "C"
bool initOboeAudio(int sample_rate, int channel_count) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
}

extern "C"
void shutdownOboeAudio() {
    RyujinxOboe::OboeAudioRenderer::GetInstance().Shutdown();
}

extern "C"
bool writeOboeAudio(const int16_t* data, int32_t num_frames) {
    if (!data || num_frames <= 0) {
        return false;
    }
    return RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(data, num_frames);
}

extern "C"
bool writeOboeAudioToStream(const float* data, int32_t num_frames, int stream_id) {
    if (!data || num_frames <= 0) {
        return false;
    }
    return RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudioToStream(data, num_frames, stream_id);
}

extern "C"
void setOboeSampleRate(int32_t sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        return;
    }
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetSampleRate(sample_rate);
}

extern "C"
void setOboeBufferSize(int32_t buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        return;
    }
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetBufferSize(buffer_size);
}

extern "C"
void setOboeVolume(float volume) {
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
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== 多流管理 C 接口 ===============
extern "C"
bool createAdditionalOboeStream() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().CreateAdditionalStream();
}

extern "C"
bool switchToOboeStream(int stream_id) {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().SwitchToStream(stream_id);
}

extern "C"
int getCurrentOboeStreamId() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().GetCurrentStreamId();
}

extern "C"
int getOboeStreamCount() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().GetStreamCount();
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