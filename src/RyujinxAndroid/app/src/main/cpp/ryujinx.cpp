#include "ryuijnx.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>
#include <memory>      // C++20: 智能指针
#include <string_view> // C++20: string_view
#include <optional>    // C++20: optional类型
#include <vector>      // C++20: 容器

// 线程安全的单例模式
namespace {
    class ThreadSafeSingleton {
    public:
        template<typename T, typename... Args>
        static std::shared_ptr<T> getInstance(Args&&... args) {
            static std::mutex mutex;
            static std::weak_ptr<T> instance;
            
            std::lock_guard<std::mutex> lock(mutex);
            auto ptr = instance.lock();
            if (!ptr) {
                ptr = std::make_shared<T>(std::forward<Args>(args)...);
                instance = ptr;
            }
            return ptr;
        }
    };
}

long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative;
std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;
bool isInitialOrientationFlipped = true;

// C++20: 使用智能指针管理全局单例实例
static std::unique_ptr<RyujinxOboe::OboeAudioRenderer> g_singleton_renderer;

// C++20: 使用RAII包装器管理字符串内存
class JNIStringRAII {
private:
    JNIEnv* m_env;
    jstring m_jstr;
    const char* m_cstr;
    
public:
    JNIStringRAII(JNIEnv* env, jstring jstr) : m_env(env), m_jstr(jstr), m_cstr(nullptr) {
        if (jstr) {
            m_cstr = env->GetStringUTFChars(jstr, nullptr);
        }
    }
    
    ~JNIStringRAII() {
        if (m_cstr && m_jstr) {
            m_env->ReleaseStringUTFChars(m_jstr, m_cstr);
        }
    }
    
    // C++20: 删除拷贝构造和赋值
    JNIStringRAII(const JNIStringRAII&) = delete;
    JNIStringRAII& operator=(const JNIStringRAII&) = delete;
    
    // C++20: 允许移动
    JNIStringRAII(JNIStringRAII&& other) noexcept 
        : m_env(other.m_env), m_jstr(other.m_jstr), m_cstr(other.m_cstr) {
        other.m_cstr = nullptr;
        other.m_jstr = nullptr;
    }
    
    [[nodiscard]] const char* get() const { return m_cstr; }
    operator bool() const { return m_cstr != nullptr; }
};

// C++20: 安全的字符串复制函数
[[nodiscard]] std::unique_ptr<char[]> copyString(const char* source) {
    if (!source) return nullptr;
    
    size_t len = strlen(source) + 1;
    auto buffer = std::make_unique<char[]>(len);
    strcpy(buffer.get(), source);
    return buffer;
}

extern "C" {

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(JNIEnv *env, jobject instance, jobject surface) {
    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    return nativeWindow == NULL ? -1 : reinterpret_cast<jlong>(nativeWindow);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(JNIEnv *env, jobject instance, jlong window) {
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(window);
    if (nativeWindow != NULL) {
        ANativeWindow_release(nativeWindow);
    }
}

long createSurface(long native_surface, long instance) {
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_surface);
    VkSurfaceKHR surface;
    auto vkInstance = reinterpret_cast<VkInstance>(instance);
    
    auto fpCreateAndroidSurfaceKHR = reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
        vkGetInstanceProcAddr(vkInstance, "vkCreateAndroidSurfaceKHR"));
    
    if (!fpCreateAndroidSurfaceKHR) return -1;
    
    VkAndroidSurfaceCreateInfoKHR info = {
        VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR,
        nullptr,
        0,
        nativeWindow
    };
    
    VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
    return reinterpret_cast<long>(surface);
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(JNIEnv *env, jobject instance) {
    return reinterpret_cast<jlong>(createSurface);
}

char *getStringPointer(JNIEnv *env, jstring jS) {
    if (!jS) return nullptr;
    
    JNIStringRAII raii(env, jS);
    if (!raii.get()) return nullptr;
    
    size_t len = strlen(raii.get());
    char* s = new char[len + 1];
    strcpy(s, raii.get());
    return s;
}

jstring createString(JNIEnv *env, char *ch) {
    if (!ch) return nullptr;
    return env->NewStringUTF(ch);
}

jstring createStringFromStdString(JNIEnv *env, std::string s) {
    return env->NewStringUTF(s.c_str());
}

void setRenderingThread() {
    _renderingThreadIdNative = pthread_self();
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) { 
    __android_log_print(ANDROID_LOG_INFO, "RyujinxNative", "JNI_OnLoad called");
    return JNI_VERSION_1_6; 
}

JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxNative", "JNI_OnUnload called");
    
    // 清理全局资源
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        g_singleton_renderer.reset();
    }
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = reinterpret_cast<jclass>(env->NewGlobalRef(env->GetObjectClass(thiz)));
}

void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1) return;
    auto nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
    transform = transform >> 1;

    switch (transform) {
        case 0x1: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
        case 0x2: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90; break;
        case 0x4: nativeTransform = isInitialOrientationFlipped ? 
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY : 
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_180; break;
        case 0x8: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_270; break;
        case 0x10: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL; break;
        case 0x20: nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL | 
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
        case 0x40: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL; break;
        case 0x80: nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | 
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
        case 0x100: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
        default: break;
    }

    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM, 
                         static_cast<int32_t>(nativeTransform));
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    // C++20: 使用RAII管理JNI字符串
    JNIStringRAII libPath(env, native_lib_path);
    JNIStringRAII privateAppsPath(env, private_apps_path);
    JNIStringRAII driverName(env, driver_name);
    
    if (!libPath.get() || !privateAppsPath.get() || !driverName.get()) {
        return 0;
    }
    
    auto handle = adrenotools_open_libvulkan(RTLD_NOW, ADRENOTOOLS_DRIVER_CUSTOM, nullptr,
                                            libPath.get(), privateAppsPath.get(), 
                                            driverName.get(), nullptr, nullptr);
    
    return reinterpret_cast<jlong>(handle);
}

void debug_break(int code) {
    if (code >= 3) { 
        // 调试断点
        __android_log_print(ANDROID_LOG_DEBUG, "RyujinxNative", "Debug break code: %d", code);
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
    auto str = reinterpret_cast<char*>(ptr);
    if (!str) return nullptr;
    
    jstring result = env->NewStringUTF(str);
    delete[] str; // 清理内存，避免泄露
    return result;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz, jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

// ========== 单例 Oboe Audio JNI接口 (保持向后兼容) ==========

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<RyujinxOboe::OboeAudioRenderer>();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count, jint sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<RyujinxOboe::OboeAudioRenderer>();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        g_singleton_renderer.reset();
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jshortArray audio_data, jint num_frames) {
    if (!g_singleton_renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (!data) return JNI_FALSE;
    
    bool success = g_singleton_renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
    env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(JNIEnv *env, jobject thiz, jbyteArray audio_data, jint num_frames, jint sample_format) {
    if (!g_singleton_renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (!data) return JNI_FALSE;
    
    bool success = g_singleton_renderer->WriteAudioRaw(reinterpret_cast<uint8_t*>(data), num_frames, sample_format);
    env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
    return success ? JNI_TRUE : JNI_FALSE;
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
    auto renderer = new (std::nothrow) RyujinxOboe::OboeAudioRenderer();
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
Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr, jint sample_rate, jint channel_count, jint sample_format) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(JNIEnv *env, jobject thiz, jlong renderer_ptr, jshortArray audio_data, jint num_frames) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (!data) return JNI_FALSE;
    
    bool success = renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
    env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(JNIEnv *env, jobject thiz, jlong renderer_ptr, jbyteArray audio_data, jint num_frames, jint sample_format) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (!data) return JNI_FALSE;
    
    bool success = renderer->WriteAudioRaw(reinterpret_cast<uint8_t*>(data), num_frames, sample_format);
    env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(JNIEnv *env, jobject thiz, jlong renderer_ptr, jfloat volume) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
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
    char model[MAX_DEVICE_INFO_LENGTH] = {0};
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[MAX_DEVICE_INFO_LENGTH] = {0};
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

} // extern "C" 结束

// ========== 单例 Oboe Audio C接口 (保持向后兼容) ==========

bool initOboeAudio(int sample_rate, int channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<RyujinxOboe::OboeAudioRenderer>();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count);
}

bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<RyujinxOboe::OboeAudioRenderer>();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeAudio() {
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        g_singleton_renderer.reset();
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

// C++20: 使用智能指针内部管理，但对外保持原始指针接口
struct OboeRendererWrapper {
    std::unique_ptr<RyujinxOboe::OboeAudioRenderer> renderer;
    
    OboeRendererWrapper() : renderer(std::make_unique<RyujinxOboe::OboeAudioRenderer>()) {}
    ~OboeRendererWrapper() {
        if (renderer) {
            renderer->Shutdown();
        }
    }
    
    // C++20: 删除拷贝构造和赋值
    OboeRendererWrapper(const OboeRendererWrapper&) = delete;
    OboeRendererWrapper& operator=(const OboeRendererWrapper&) = delete;
    
    // C++20: 允许移动
    OboeRendererWrapper(OboeRendererWrapper&&) = default;
    OboeRendererWrapper& operator=(OboeRendererWrapper&&) = default;
};

void* createOboeRenderer() {
    try {
        auto wrapper = new OboeRendererWrapper();
        return wrapper;
    } catch (const std::exception& e) {
        __android_log_print(ANDROID_LOG_ERROR, "RyujinxNative", 
                          "Failed to create Oboe renderer: %s", e.what());
        return nullptr;
    }
}

void destroyOboeRenderer(void* renderer) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    delete wrapper;
}

bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    return wrapper && wrapper->renderer && 
           wrapper->renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeRenderer(void* renderer) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    if (wrapper && wrapper->renderer) {
        wrapper->renderer->Shutdown();
    }
}

bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    return wrapper && wrapper->renderer && data && num_frames > 0 && 
           wrapper->renderer->WriteAudio(data, num_frames);
}

bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, int32_t sample_format) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    return wrapper && wrapper->renderer && data && num_frames > 0 && 
           wrapper->renderer->WriteAudioRaw(data, num_frames, sample_format);
}

void setOboeRendererVolume(void* renderer, float volume) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    if (wrapper && wrapper->renderer) {
        wrapper->renderer->SetVolume(volume);
    }
}

bool isOboeRendererInitialized(void* renderer) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    return wrapper && wrapper->renderer && wrapper->renderer->IsInitialized();
}

bool isOboeRendererPlaying(void* renderer) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    return wrapper && wrapper->renderer && wrapper->renderer->IsPlaying();
}

int32_t getOboeRendererBufferedFrames(void* renderer) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    return wrapper && wrapper->renderer ? 
           static_cast<int32_t>(wrapper->renderer->GetBufferedFrames()) : 0;
}

void resetOboeRenderer(void* renderer) {
    auto wrapper = reinterpret_cast<OboeRendererWrapper*>(renderer);
    if (wrapper && wrapper->renderer) {
        wrapper->renderer->Reset();
    }
}

const char* GetAndroidDeviceModel() {
    static char model[MAX_DEVICE_INFO_LENGTH] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
    }
    return model;
}

const char* GetAndroidDeviceBrand() {
    static char brand[MAX_DEVICE_INFO_LENGTH] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
    }
    return brand;
}
