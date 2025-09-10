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

extern "C" {
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
        JNIEnv *env,
        jobject instance,
        jobject surface) {
    if (!surface) {
        return -1;
    }

    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    if (nativeWindow == nullptr) {
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
        return -1;
    }

    VkSurfaceKHR surface = VK_NULL_HANDLE;
    auto vkInstance = (VkInstance) instance;
    if (vkInstance == VK_NULL_HANDLE) {
        return -1;
    }

    auto vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(RTLD_DEFAULT, "vkGetInstanceProcAddr");
    if (!vkGetInstanceProcAddr) {
        return -1;
    }

    auto fpCreateAndroidSurfaceKHR = (PFN_vkCreateAndroidSurfaceKHR)vkGetInstanceProcAddr(
        vkInstance, "vkCreateAndroidSurfaceKHR");

    if (!fpCreateAndroidSurfaceKHR) {
        return -1;
    }

    VkAndroidSurfaceCreateInfoKHR info = {};
    info.sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR;
    info.window = nativeWindow;

    VkResult result = fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface);
    if (result != VK_SUCCESS) {
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

static char *getStringPointer(JNIEnv *env, jstring jS) {
    if (!jS) return nullptr;

    const char *cparam = env->GetStringUTFChars(jS, 0);
    if (!cparam) {
        return nullptr;
    }

    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    if (!s) {
        env->ReleaseStringUTFChars(jS, cparam);
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
        return;
    }
    _vm = vm;

    _mainActivity = env->NewGlobalRef(thiz);
    if (!_mainActivity) {
        return;
    }

    jclass localClass = env->GetObjectClass(thiz);
    _mainActivityClass = (jclass)env->NewGlobalRef(localClass);
    if (!_mainActivityClass) {
        env->DeleteGlobalRef(_mainActivity);
        _mainActivity = nullptr;
        return;
    }

    env->DeleteLocalRef(localClass);
}

bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1) {
        return;
    }

    auto nativeWindow = (ANativeWindow *) native_window;
    int32_t nativeTransform = 0;

    switch (transform) {
        case 1: nativeTransform = 0; break;
        case 2: nativeTransform = 1; break;
        case 4: nativeTransform = isInitialOrientationFlipped ? 0 : 2; break;
        case 8: nativeTransform = 3; break;
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

    if (!libPath || !privateAppsPath || !driverName) {
        goto cleanup;
    }

    auto handle = adrenotools_open_libvulkan(
            RTLD_NOW,
            1,
            nullptr,
            libPath,
            privateAppsPath,
            driverName,
            nullptr,
            nullptr
    );

cleanup:
    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;

    return (jlong) handle;
}

extern "C"
void debug_break(int code) {
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

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz) {
    OboeAudioRenderer::getInstance().initialize();
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
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
        return;
    }

    jfloat* data = env->GetFloatArrayElements(audio_data, nullptr);
    if (!data) {
        return;
    }

    OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
    env->ReleaseFloatArrayElements(audio_data, data, JNI_ABORT);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeSampleRate(JNIEnv *env, jobject thiz, jint sample_rate) {
    if (sample_rate < 8000 || sample_rate > 192000) {
        return;
    }
    OboeAudioRenderer::getInstance().setSampleRate(sample_rate);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeBufferSize(JNIEnv *env, jobject thiz, jint buffer_size) {
    if (buffer_size < 64 || buffer_size > 8192) {
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
    bool initialized = OboeAudioRenderer::getInstance().isInitialized();
    return initialized ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    int frames = static_cast<jint>(OboeAudioRenderer::getInstance().getBufferedFrames());
    return frames;
}

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

extern "C"
void initOboeAudio() {
    OboeAudioRenderer::getInstance().initialize();
}

extern "C"
void shutdownOboeAudio() {
    OboeAudioRenderer::getInstance().shutdown();
}

extern "C"
void writeOboeAudio(float* audioData, int num_frames, int input_channels, int output_channels) {
    if (!audioData || num_frames <= 0) {
        return;
    }
    OboeAudioRenderer::getInstance().writeAudio(audioData, num_frames);
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
void setOboeNoiseShapingEnabled(bool enabled) {
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

extern "C" {
int32_t ANativeWindow_setBuffersTransform(ANativeWindow* window, int32_t transform) {
    return 0;
}
}
