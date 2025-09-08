// ryujinx.cpp
#include "ryuijnx.h"
#include "pthread.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

extern "C" {

// =============== 原有 Vulkan / Surface / AdrenoTools 代码保持不变 ===============
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

char *getStringPointer(JNIEnv *env, jstring jS) {
    const char *cparam = env->GetStringUTFChars(jS, 0);
    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    strcpy(s, cparam);
    env->ReleaseStringUTFChars(jS, cparam);
    return s;
}

jstring createString(JNIEnv *env, char *ch) {
    return env->NewStringUTF(ch);
}

jstring createStringFromStdString(JNIEnv *env, std::string s) {
    return env->NewStringUTF(s.c_str());
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
    if (native_window == 0 || native_window == -1) return;
    auto nativeWindow = (ANativeWindow *) native_window;

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
    transform = transform >> 1;

    switch (transform) {
        case 0x1: nativeTransform = ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
        case 0x2: nativeTransform = ANATIVEWINDOW_TRANSFORM_ROTATE_90; break;
        case 0x4: nativeTransform = isInitialOrientationFlipped
                    ? ANATIVEWINDOW_TRANSFORM_IDENTITY
                    : ANATIVEWINDOW_TRANSFORM_ROTATE_180; break;
        case 0x8: nativeTransform = ANATIVEWINDOW_TRANSFORM_ROTATE_270; break;
        case 0x10: nativeTransform = ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL; break;
        case 0x20: nativeTransform = static_cast<ANativeWindowTransform>(
                    ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
        case 0x40: nativeTransform = ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL; break;
        case 0x80: nativeTransform = static_cast<ANativeWindowTransform>(
                    ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
        case 0x100: nativeTransform = ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
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

// =============== Oboe Audio JNI 接口 ===============
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Initializing Oboe audio");
    OboeAudioRenderer::getInstance().initialize();
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Oboe audio initialized");
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Shutting down Oboe audio");
    OboeAudioRenderer::getInstance().shutdown();
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Oboe audio shutdown complete");
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jfloatArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        __android_log_write(ANDROID_LOG_WARN, "OboeAudio", "Invalid audio data or frame count");
        return;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (data) {
        __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Writing %d frames to Oboe", num_frames);
        OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
        env->ReleaseFloatArrayElements(audio_data, data, 0);
        __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Audio data written successfully");
    } else {
        __android_log_write(ANDROID_LOG_ERROR, "OboeAudio", "Failed to get audio data array");
    }
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        __android_log_print(ANDROID_LOG_WARN, "OboeAudio", "Invalid sample rate: %d", sample_rate);
        return;
    }
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Setting sample rate to %d", sample_rate);
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Sample rate set successfully");
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        __android_log_print(ANDROID_LOG_WARN, "OboeAudio", "Invalid buffer size: %d", buffer_size);
        return;
    }
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Setting buffer size to %d", buffer_size);
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Buffer size set successfully");
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Setting volume to %.2f", volume);
    OboeAudioRenderer::getInstance().setVolume(volume);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Volume set successfully");
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    bool initialized = OboeAudioRenderer::getInstance().isInitialized();
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Oboe initialized check: %s", initialized ? "true" : "false");
    return initialized;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    int32_t bufferedFrames = (int32_t)OboeAudioRenderer::getInstance().getBufferedFrames();
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Buffered frames: %d", bufferedFrames);
    return (jint)bufferedFrames;
}

// =============== Oboe Audio C 接口 (for C# P/Invoke) ===============
void initOboeAudio() {
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Initializing Oboe audio (C interface)");
    OboeAudioRenderer::getInstance().initialize();
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Oboe audio initialized (C interface)");
}

void shutdownOboeAudio() {
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Shutting down Oboe audio (C interface)");
    OboeAudioRenderer::getInstance().shutdown();
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Oboe audio shutdown complete (C interface)");
}

void writeOboeAudio(const float* data, int32_t num_frames) {
    if (!data || num_frames <= 0) {
        __android_log_write(ANDROID_LOG_WARN, "OboeAudio", "Invalid audio data or frame count (C interface)");
        return;
    }
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Writing %d frames to Oboe (C interface)", num_frames);
    OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Audio data written successfully (C interface)");
}

void setOboeSampleRate(int32_t sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        __android_log_print(ANDROID_LOG_WARN, "OboeAudio", "Invalid sample rate: %d (C interface)", sample_rate);
        return;
    }
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Setting sample rate to %d (C interface)", sample_rate);
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Sample rate set successfully (C interface)");
}

void setOboeBufferSize(int32_t buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
        __android_log_print(ANDROID_LOG_WARN, "OboeAudio", "Invalid buffer size: %d (C interface)", buffer_size);
        return;
    }
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Setting buffer size to %d (C interface)", buffer_size);
    OboeAudioRenderer::getInstance().setBufferSize(buffer_size);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Buffer size set successfully (C interface)");
}

void setOboeVolume(float volume) {
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Setting volume to %.2f (C interface)", volume);
    OboeAudioRenderer::getInstance().setVolume(volume);
    __android_log_write(ANDROID_LOG_DEBUG, "OboeAudio", "Volume set successfully (C interface)");
}

bool isOboeInitialized() {
    bool initialized = OboeAudioRenderer::getInstance().isInitialized();
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Oboe initialized check: %s (C interface)", initialized ? "true" : "false");
    return initialized;
}

int32_t getOboeBufferedFrames() {
    int32_t bufferedFrames = (int32_t)OboeAudioRenderer::getInstance().getBufferedFrames();
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", "Buffered frames: %d (C interface)", bufferedFrames);
    return bufferedFrames;
}

} // extern "C"
