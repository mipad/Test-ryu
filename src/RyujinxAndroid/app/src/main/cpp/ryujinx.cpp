// ryujinx.cpp (完整实现)
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

// JNI_OnLoad
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    return JNI_VERSION_1_6;
}

// JNI_OnUnload
JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
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

// =============== Oboe Audio JNI 接口 ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    bool result = RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
    return result ? JNI_TRUE : JNI_FALSE;
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
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    
    if (data) {
        bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    
    return JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
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
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== PCM Offload JNI 接口 ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_enableOboePcmOffload(JNIEnv *env, jobject thiz, jboolean enable) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().EnablePcmOffload(enable);
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePcmOffloadEnabled(JNIEnv *env, jobject thiz) {
    bool enabled = RyujinxOboe::OboeAudioRenderer::GetInstance().IsPcmOffloadEnabled();
    return enabled ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePcmOffloadSupported(JNIEnv *env, jobject thiz) {
    bool supported = RyujinxOboe::OboeAudioRenderer::GetInstance().IsPcmOffloadSupported();
    return supported ? JNI_TRUE : JNI_FALSE;
}

// =============== 压缩音频 JNI 接口 ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeCompressedAudio(JNIEnv *env, jobject thiz, 
                                                               jbyteArray audio_data, 
                                                               jint data_size,
                                                               jint format,
                                                               jint num_frames) {
    if (!audio_data || data_size <= 0) {
        return JNI_FALSE;
    }

    jsize length = env->GetArrayLength(audio_data);
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    
    if (data) {
        bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteCompressedAudio(
            reinterpret_cast<uint8_t*>(data), 
            data_size,
            static_cast<oboe::AudioFormat>(format),
            num_frames
        );
        env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    
    return JNI_FALSE;
}

// =============== 高级音频配置 JNI 接口 ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboePerformanceMode(JNIEnv *env, jobject thiz, jint performance_mode) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetPerformanceMode(static_cast<oboe::PerformanceMode>(performance_mode));
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeUsage(JNIEnv *env, jobject thiz, jint usage) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetUsage(static_cast<oboe::Usage>(usage));
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeContentType(JNIEnv *env, jobject thiz, jint content_type) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetContentType(static_cast<oboe::ContentType>(content_type));
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeChannelMask(JNIEnv *env, jobject thiz, jint channel_mask) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetChannelMask(static_cast<oboe::ChannelMask>(channel_mask));
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferCapacity(JNIEnv *env, jobject thiz, jint capacity_frames) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetBufferCapacity(capacity_frames);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_enableOboeMmap(JNIEnv *env, jobject thiz, jboolean enable) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().EnableMmap(enable);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeAudioFocus(JNIEnv *env, jobject thiz, jboolean has_focus) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetAudioFocus(has_focus);
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
    bool result = RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
    return result;
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
    
    bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(data, num_frames);
    return success;
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

// =============== PCM Offload C 接口 ===============
extern "C"
void enableOboePcmOffload(bool enable) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().EnablePcmOffload(enable);
}

extern "C"
bool isOboePcmOffloadEnabled() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsPcmOffloadEnabled();
}

extern "C"
bool isOboePcmOffloadSupported() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsPcmOffloadSupported();
}

// =============== 压缩音频 C 接口 ===============
extern "C"
bool writeOboeCompressedAudio(const uint8_t* data, size_t data_size, 
                             int format, int32_t num_frames) {
    if (!data || data_size == 0) {
        return false;
    }
    
    bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteCompressedAudio(
        data, data_size, static_cast<oboe::AudioFormat>(format), num_frames);
    return success;
}

// =============== 高级音频配置 C 接口 ===============
extern "C"
void setOboePerformanceMode(int performance_mode) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetPerformanceMode(static_cast<oboe::PerformanceMode>(performance_mode));
}

extern "C"
void setOboeUsage(int usage) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetUsage(static_cast<oboe::Usage>(usage));
}

extern "C"
void setOboeContentType(int content_type) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetContentType(static_cast<oboe::ContentType>(content_type));
}

extern "C"
void setOboeChannelMask(int channel_mask) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetChannelMask(static_cast<oboe::ChannelMask>(channel_mask));
}

extern "C"
void setOboeBufferCapacity(int capacity_frames) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetBufferCapacity(capacity_frames);
}

extern "C"
void enableOboeMmap(bool enable) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().EnableMmap(enable);
}

extern "C"
void setOboeAudioFocus(bool has_focus) {
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetAudioFocus(has_focus);
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