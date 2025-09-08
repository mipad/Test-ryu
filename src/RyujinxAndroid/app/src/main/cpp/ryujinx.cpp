// ryujinx.cpp (完整版，含设备信息 JNI 函数)
#include "ryuijnx.h"
#include "pthread.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <sys/system_properties.h> // 用于获取设备属性

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

void setRenderingThread() {
    auto currentId = pthread_self();

    _renderingThreadId = currentId;

    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

bool isInitialOrientationFlipped = true;

void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1)
        return;
    auto nativeWindow = (ANativeWindow *) native_window;

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;

    transform = transform >> 1;

    switch (transform) {
        case 0x1:
            nativeTransform = ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
        case 0x2:
            nativeTransform = ANATIVEWINDOW_TRANSFORM_ROTATE_90;
            break;
        case 0x4:
            nativeTransform = isInitialOrientationFlipped
                              ? ANATIVEWINDOW_TRANSFORM_IDENTITY
                              : ANATIVEWINDOW_TRANSFORM_ROTATE_180;
            break;
        case 0x8:
            nativeTransform = ANATIVEWINDOW_TRANSFORM_ROTATE_270;
            break;
        case 0x10:
            nativeTransform = ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL;
            break;
        case 0x20:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x40:
            nativeTransform = ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
            break;
        case 0x80:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x100:
            nativeTransform = ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
    }

    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM,
                          static_cast<int32_t>(nativeTransform));
}

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

void debug_break(int code) {
    if (code >= 3)
        int r = 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable);
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->maxSwapInterval;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->minSwapInterval;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->setSwapInterval(nativeWindow, swap_interval);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    return createString(env, (char*)ptr);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

// ========== Oboe 音频 JNI 接口 ===============
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz) {
    OboeAudioRenderer::getInstance().initialize();
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    OboeAudioRenderer::getInstance().shutdown();
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jfloatArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        return;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (data) {
        OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
        env->ReleaseFloatArrayElements(audio_data, data, 0);
    }
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) return;
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) return;
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    OboeAudioRenderer::getInstance().setVolume(volume);
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    return OboeAudioRenderer::getInstance().isInitialized();
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    return (jint)OboeAudioRenderer::getInstance().getBufferedFrames();
}

// ========== 设备信息 JNI 函数 ===============
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char device[PROP_VALUE_MAX];
    __system_property_get("ro.product.device", device);
    return env->NewStringUTF(device);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX];
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

// ========== Oboe 音频 C 接口 (for C# P/Invoke) ===============
void initOboeAudio() {
    OboeAudioRenderer::getInstance().initialize();
}

void shutdownOboeAudio() {
    OboeAudioRenderer::getInstance().shutdown();
}

void writeOboeAudio(const float* data, int32_t num_frames) {
    if (!data || num_frames <= 0) return;
    OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
}

void setOboeSampleRate(int32_t sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) return;
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

void setOboeBufferSize(int32_t buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) return;
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
}

void setOboeVolume(float volume) {
    OboeAudioRenderer::getInstance().setVolume(volume);
}

bool isOboeInitialized() {
    return OboeAudioRenderer::getInstance().isInitialized();
}

int32_t getOboeBufferedFrames() {
    return (int32_t)OboeAudioRenderer::getInstance().getBufferedFrames();
}

} // extern "C"
