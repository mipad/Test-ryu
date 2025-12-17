#include "ryuijnx.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <cstdarg>
#include <sys/system_properties.h>
#include <cstring>
#include <memory>

// 全局变量定义
long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative = 0;
std::chrono::time_point<std::chrono::steady_clock> _currentTimePoint;
bool isInitialOrientationFlipped = true;

// 全局单例实例
static RyujinxOboe::OboeAudioRenderer* g_singleton_renderer = nullptr;

// 匿名命名空间，避免全局命名冲突
namespace {
    [[nodiscard]] long createSurfaceImpl(long native_surface, long instance) {
        auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_surface);
        VkSurfaceKHR surface = VK_NULL_HANDLE;
        auto vkInstance = reinterpret_cast<VkInstance>(instance);
        
        auto fpCreateAndroidSurfaceKHR = reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
            vkGetInstanceProcAddr(vkInstance, "vkCreateAndroidSurfaceKHR"));
        
        if (!fpCreateAndroidSurfaceKHR) return -1;
        
        VkAndroidSurfaceCreateInfoKHR info = {
            .sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR,
            .pNext = nullptr,
            .flags = 0,
            .window = nativeWindow
        };
        
        VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
        return reinterpret_cast<long>(surface);
    }

    void setCurrentTransformImpl(long native_window, int transform) {
        if (native_window == 0 || native_window == -1) return;
        
        auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
        auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
        transform >>= 1; // 移除以匹配转换值
        
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
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90);
                break;
            case 0x40:
                nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
                break;
            case 0x80:
                nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | 
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90);
                break;
            case 0x100:
                nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
                break;
        }
        
        nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM, 
                              static_cast<int32_t>(nativeTransform));
    }
}

extern "C" {

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(JNIEnv *env, jobject instance, jobject surface) {
    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    return nativeWindow == nullptr ? -1 : reinterpret_cast<jlong>(nativeWindow);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(JNIEnv *env, jobject instance, jlong window) {
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(window);
    if (nativeWindow != nullptr) {
        ANativeWindow_release(nativeWindow);
    }
}

long createSurface(long native_surface, long instance) {
    return createSurfaceImpl(native_surface, instance);
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(JNIEnv *env, jobject instance) {
    return reinterpret_cast<jlong>(createSurface);
}

[[nodiscard]] char *getStringPointer(JNIEnv *env, jstring jS) {
    const char *cparam = env->GetStringUTFChars(jS, nullptr);
    auto len = env->GetStringUTFLength(jS);
    auto s = std::make_unique<char[]>(len + 1);
    std::strcpy(s.get(), cparam);
    env->ReleaseStringUTFChars(jS, cparam);
    return s.release(); // 调用者需要delete[]释放内存
}

[[nodiscard]] jstring createString(JNIEnv *env, char *ch) {
    return env->NewStringUTF(ch);
}

[[nodiscard]] jstring createStringFromStdString(JNIEnv *env, std::string s) {
    return env->NewStringUTF(s.c_str());
}

void setRenderingThread() {
    _renderingThreadIdNative = pthread_self();
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) { 
    return JNI_VERSION_1_6; 
}

JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    env->GetJavaVM(&_vm);
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

void setCurrentTransform(long native_window, int transform) {
    setCurrentTransformImpl(native_window, transform);
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    auto libPath = getStringPointer(env, native_lib_path);
    auto privateAppsPath = getStringPointer(env, private_apps_path);
    auto driverName = getStringPointer(env, driver_name);
    
    auto handle = adrenotools_open_libvulkan(RTLD_NOW, ADRENOTOOLS_DRIVER_CUSTOM, nullptr,
                                             libPath, privateAppsPath, driverName, nullptr, nullptr);
    
    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;
    return reinterpret_cast<jlong>(handle);
}

void debug_break(int code) {
    if (code >= 3) { 
        // 调试断点逻辑
    }
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable);
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz, jlong native_window) {
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    return nativeWindow->maxSwapInterval;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz, jlong native_window) {
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    return nativeWindow->minSwapInterval;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz, jlong native_window, jint swap_interval) {
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    return nativeWindow->setSwapInterval(nativeWindow, swap_interval);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    return createString(env, reinterpret_cast<char*>(ptr));
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz, jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

// ========== 单例 Oboe Audio JNI接口 ==========

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(JNIEnv *env, jobject thiz, 
                                                               jint sample_rate, jint channel_count, jint sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        delete g_singleton_renderer;
        g_singleton_renderer = nullptr;
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, 
                                                      jshortArray audio_data, jint num_frames) {
    if (!g_singleton_renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (data) {
        bool success = g_singleton_renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(JNIEnv *env, jobject thiz, 
                                                         jbyteArray audio_data, jint num_frames, jint sample_format) {
    if (!g_singleton_renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (data) {
        bool success = g_singleton_renderer->WriteAudioRaw(reinterpret_cast<uint8_t*>(data), 
                                                          num_frames, sample_format);
        env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    if (g_singleton_renderer) {
        g_singleton_renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    return g_singleton_renderer && g_singleton_renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(JNIEnv *env, jobject thiz) {
    return g_singleton_renderer && g_singleton_renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    return g_singleton_renderer ? static_cast<jint>(g_singleton_renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(JNIEnv *env, jobject thiz) {
    if (g_singleton_renderer) {
        g_singleton_renderer->Reset();
    }
}

// ========== 多实例 Oboe Audio JNI接口 ==========

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createOboeRenderer(JNIEnv *env, jobject thiz) {
    auto renderer = new RyujinxOboe::OboeAudioRenderer();
    return reinterpret_cast<jlong>(renderer);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
        delete renderer;
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(JNIEnv *env, jobject thiz, 
                                                        jlong renderer_ptr, jint sample_rate, 
                                                        jint channel_count, jint sample_format) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) 
           ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(JNIEnv *env, jobject thiz, 
                                                              jlong renderer_ptr, jshortArray audio_data, 
                                                              jint num_frames) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (data) {
        bool success = renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(JNIEnv *env, jobject thiz, 
                                                                 jlong renderer_ptr, jbyteArray audio_data, 
                                                                 jint num_frames, jint sample_format) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (data) {
        bool success = renderer->WriteAudioRaw(reinterpret_cast<uint8_t*>(data), 
                                              num_frames, sample_format);
        env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(JNIEnv *env, jobject thiz, 
                                                             jlong renderer_ptr, jfloat volume) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(JNIEnv *env, jobject thiz, 
                                                                 jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(JNIEnv *env, jobject thiz, 
                                                             jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(JNIEnv *env, jobject thiz, 
                                                                     jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer ? static_cast<jint>(renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Reset();
    }
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char model[PROP_VALUE_MAX];
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX];
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

} // extern "C"

// ========== 单例 Oboe Audio C接口 ==========

bool initOboeAudio(int sample_rate, int channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count);
}

bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeAudio() {
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        delete g_singleton_renderer;
        g_singleton_renderer = nullptr;
    }
}

bool writeOboeAudio(const int16_t* data, int32_t num_frames) {
    return g_singleton_renderer && data && num_frames > 0 && 
           g_singleton_renderer->WriteAudio(data, num_frames);
}

bool writeOboeAudioRaw(const uint8_t* data, int32_t num_frames, int32_t sample_format) {
    return g_singleton_renderer && data && num_frames > 0 && 
           g_singleton_renderer->WriteAudioRaw(data, num_frames, sample_format);
}

void setOboeVolume(float volume) {
    if (g_singleton_renderer) {
        g_singleton_renderer->SetVolume(volume);
    }
}

bool isOboeInitialized() {
    return g_singleton_renderer && g_singleton_renderer->IsInitialized();
}

bool isOboePlaying() {
    return g_singleton_renderer && g_singleton_renderer->IsPlaying();
}

int32_t getOboeBufferedFrames() {
    return g_singleton_renderer ? static_cast<int32_t>(g_singleton_renderer->GetBufferedFrames()) : 0;
}

void resetOboeAudio() {
    if (g_singleton_renderer) {
        g_singleton_renderer->Reset();
    }
}

// ========== 多实例 Oboe Audio C接口 ==========

void* createOboeRenderer() {
    return new RyujinxOboe::OboeAudioRenderer();
}

void destroyOboeRenderer(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
        delete oboe_renderer;
    }
}

bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeRenderer(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
    }
}

bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && data && num_frames > 0 && 
           oboe_renderer->WriteAudio(data, num_frames);
}

bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, 
                               int32_t sample_format) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && data && num_frames > 0 && 
           oboe_renderer->WriteAudioRaw(data, num_frames, sample_format);
}

void setOboeRendererVolume(void* renderer, float volume) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->SetVolume(volume);
    }
}

bool isOboeRendererInitialized(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsInitialized();
}

bool isOboeRendererPlaying(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsPlaying();
}

int32_t getOboeRendererBufferedFrames(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer ? static_cast<int32_t>(oboe_renderer->GetBufferedFrames()) : 0;
}

void resetOboeRenderer(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Reset();
    }
}

const char* GetAndroidDeviceModel() {
    static char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
    }
    return model;
}

const char* GetAndroidDeviceBrand() {
    static char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
    }
    return brand;
}