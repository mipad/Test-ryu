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
#include <android/asset_manager.h>
#include <android/asset_manager_jni.h>

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;
static AAssetManager* assetManager = nullptr;

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

// =============== 新增：着色器加载函数 ===============
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initAssetManager(
        JNIEnv *env,
        jobject instance,
        jobject asset_manager) {
    assetManager = AAssetManager_fromJava(env, asset_manager);
    __android_log_print(ANDROID_LOG_INFO, "RyujinxNative", "AssetManager initialized");
}

JNIEXPORT jbyteArray JNICALL
Java_org_ryujinx_android_NativeHelpers_loadShaderFromAssets(
        JNIEnv *env,
        jobject instance,
        jstring shader_path) {
    if (assetManager == nullptr) {
        __android_log_print(ANDROID_LOG_ERROR, "RyujinxNative", "AssetManager not initialized");
        return nullptr;
    }

    const char* path = env->GetStringUTFChars(shader_path, nullptr);
    if (!path) {
        return nullptr;
    }

    AAsset* asset = AAssetManager_open(assetManager, path, AASSET_MODE_BUFFER);
    env->ReleaseStringUTFChars(shader_path, path);

    if (!asset) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxNative", "Shader asset not found: %s", path);
        return nullptr;
    }

    off_t length = AAsset_getLength(asset);
    jbyteArray result = env->NewByteArray(length);

    if (result) {
        const void* data = AAsset_getBuffer(asset);
        env->SetByteArrayRegion(result, 0, length, static_cast<const jbyte*>(data));
        __android_log_print(ANDROID_LOG_INFO, "RyujinxNative", "Loaded shader: %s, size: %ld", path, length);
    }

    AAsset_close(asset);
    return result;
}

// C接口版本，供C# P/Invoke调用
extern "C"
void* loadShaderFromAssets(const char* shaderPath, int* outLength) {
    if (assetManager == nullptr || shaderPath == nullptr) {
        return nullptr;
    }

    AAsset* asset = AAssetManager_open(assetManager, shaderPath, AASSET_MODE_BUFFER);
    if (!asset) {
        return nullptr;
    }

    off_t length = AAsset_getLength(asset);
    void* buffer = malloc(length);
    
    if (buffer) {
        const void* data = AAsset_getBuffer(asset);
        memcpy(buffer, data, length);
        *outLength = length;
    }

    AAsset_close(asset);
    return buffer;
}

extern "C"
void freeShaderData(void* data) {
    if (data) {
        free(data);
    }
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
    if (data) {
        OboeAudioRenderer::getInstance().writeAudio(data, num_frames);
        env->ReleaseFloatArrayElements(audio_data, data, JNI_ABORT);
    }
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
    return OboeAudioRenderer::getInstance().isInitialized();
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    return static_cast<jint>(OboeAudioRenderer::getInstance().getBufferedFrames());
}

// =============== 新增：设备信息获取函数 ===============
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
void initOboeAudio() {
    OboeAudioRenderer::getInstance().initialize();
}

extern "C"
void shutdownOboeAudio() {
    OboeAudioRenderer::getInstance().shutdown();
}

extern "C"
void writeOboeAudio(const float* data, int32_t num_frames) {
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

// =============== 新增：设备信息获取 C 接口 ===============
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
