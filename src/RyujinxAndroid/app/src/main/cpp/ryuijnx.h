#ifndef RYUJINX_HPP
#define RYUJINX_HPP

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <span>
#include <memory>
#include <atomic>
#include <functional>
#include <chrono>
#include <jni.h>
#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include "vulkan_wrapper.h"
#include <vulkan/vulkan_android.h>
#include <cassert>
#include <dlfcn.h>
#include "adrenotools/driver.h"
#include "native_window.h"
#include <pthread.h>
#include <type_traits>
#include <concepts>
#include <optional>
#include <expected>
#include <variant>
#include <thread>
#include <stop_token>
#include <semaphore>
#include <barrier>
#include <latch>

// 编译时检查
static_assert(__cplusplus >= 202002L, "Requires C++20 or later");
static_assert(sizeof(jlong) == sizeof(void*), "jlong size mismatch");

// 宏定义简化
#define VK_CHECK(x) do { VkResult result = (x); if (result != VK_SUCCESS) { \
    __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Vulkan error: %d at %s:%d", \
                       result, __FILE__, __LINE__); assert(false); } } while(0)

#define JNI_SAFE_FUNC(func_call) \
    [&]() -> auto { \
        try { return func_call; } \
        catch (const std::exception& e) { \
            __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", "Exception: %s", e.what()); \
            return decltype(func_call){}; \
        } \
    }()

// 概念定义
template<typename T>
concept VulkanHandle = std::is_pointer_v<T> || std::is_integral_v<T>;

template<typename T>
concept JNINativeObject = requires(T obj) {
    { obj.ToJNI() } -> std::same_as<jobject>;
};

template<typename T>
concept ThreadSafe = requires(T obj) {
    { obj.lock() } -> std::same_as<void>;
    { obj.unlock() } -> std::same_as<void>;
};

// 元编程辅助
template<typename... Ts>
struct overloaded : Ts... { using Ts::operator()...; };

template<typename... Ts>
overloaded(Ts...) -> overloaded<Ts...>;

// RAII包装器
template<auto Deleter, typename Handle>
class ScopedHandle {
    Handle handle_;
    
public:
    ScopedHandle(Handle handle = {}) : handle_(handle) {}
    ~ScopedHandle() { if (handle_) Deleter(handle_); }
    
    // 删除拷贝
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    
    // 允许移动
    ScopedHandle(ScopedHandle&& other) noexcept : handle_(other.handle_) {
        other.handle_ = {};
    }
    
    ScopedHandle& operator=(ScopedHandle&& other) noexcept {
        if (this != &other) {
            if (handle_) Deleter(handle_);
            handle_ = other.handle_;
            other.handle_ = {};
        }
        return *this;
    }
    
    Handle get() const { return handle_; }
    explicit operator bool() const { return handle_ != Handle{}; }
    
    Handle release() {
        Handle temp = handle_;
        handle_ = {};
        return temp;
    }
};

// JNI字符串RAII包装器
class JNIString {
    JNIEnv* env_;
    jstring jstr_;
    const char* cstr_;
    
public:
    JNIString(JNIEnv* env, jstring jstr) 
        : env_(env), jstr_(jstr), 
          cstr_(jstr ? env->GetStringUTFChars(jstr, nullptr) : nullptr) {}
    
    ~JNIString() {
        if (jstr_ && cstr_) {
            env_->ReleaseStringUTFChars(jstr_, cstr_);
        }
    }
    
    // 删除拷贝
    JNIString(const JNIString&) = delete;
    JNIString& operator=(const JNIString&) = delete;
    
    // 允许移动
    JNIString(JNIString&& other) noexcept 
        : env_(other.env_), jstr_(other.jstr_), cstr_(other.cstr_) {
        other.jstr_ = nullptr;
        other.cstr_ = nullptr;
    }
    
    const char* c_str() const { return cstr_; }
    std::string_view view() const { return cstr_ ? std::string_view(cstr_) : std::string_view(); }
    explicit operator bool() const { return cstr_ != nullptr; }
    
    operator std::string() const { return cstr_ ? std::string(cstr_) : std::string(); }
};

// 智能指针别名
using NativeWindowPtr = std::unique_ptr<ANativeWindow, decltype(&ANativeWindow_release)>;
using VkSurfacePtr = ScopedHandle<vkDestroySurfaceKHR, VkSurfaceKHR>;
using LibHandlePtr = std::unique_ptr<void, decltype(&dlclose)>;

// 线程安全全局变量
namespace Global {
    inline std::atomic<pthread_t> rendering_thread{0};
    inline std::atomic<JavaVM*> vm{nullptr};
    inline std::atomic<jobject> main_activity{nullptr};
    inline std::atomic<jclass> main_activity_class{nullptr};
    inline std::chrono::steady_clock::time_point current_time_point = std::chrono::steady_clock::now();
    inline std::atomic<bool> is_initial_orientation_flipped{true};
    
    // 线程安全的单例渲染器
    class RendererSingleton {
        std::unique_ptr<class OboeAudioRenderer> renderer_;
        std::mutex mutex_;
        
    public:
        OboeAudioRenderer* get_or_create() {
            std::lock_guard lock(mutex_);
            if (!renderer_) {
                renderer_ = std::make_unique<OboeAudioRenderer>();
            }
            return renderer_.get();
        }
        
        void destroy() {
            std::lock_guard lock(mutex_);
            renderer_.reset();
        }
        
        OboeAudioRenderer* get() const {
            return renderer_.get();
        }
    };
    
    inline RendererSingleton oboe_renderer;
}

// 前向声明
class OboeAudioRenderer;

// 外部C接口
extern "C" {
    // JNI函数声明
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_getNativeWindow(JNIEnv*, jobject, jobject);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(JNIEnv*, jobject, jlong);
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(JNIEnv*, jobject);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setRenderingThread(JNIEnv*, jobject);
    JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM*, void*);
    JNIEXPORT void JNICALL JNI_OnUnload(JavaVM*, void*);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_MainActivity_initVm(JNIEnv*, jobject);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setCurrentTransform(JNIEnv*, jobject, jlong, jint);
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv*, jobject, jstring, jstring, jstring);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv*, jobject, jboolean);
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv*, jobject, jlong);
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv*, jobject, jlong);
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv*, jobject, jlong, jint);
    JNIEXPORT jstring JNICALL Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv*, jobject, jlong);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv*, jobject, jboolean);
    
    // Oboe音频接口
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv*, jobject, jint, jint);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(JNIEnv*, jobject, jint, jint, jint);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv*, jobject);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv*, jobject, jshortArray, jint);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(JNIEnv*, jobject, jbyteArray, jint, jint);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv*, jobject, jfloat);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv*, jobject);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboePlaying(JNIEnv*, jobject);
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv*, jobject);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(JNIEnv*, jobject);
    
    // 多实例Oboe
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_createOboeRenderer(JNIEnv*, jobject);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_destroyOboeRenderer(JNIEnv*, jobject, jlong);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(JNIEnv*, jobject, jlong, jint, jint, jint);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(JNIEnv*, jobject, jlong);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(JNIEnv*, jobject, jlong, jshortArray, jint);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(JNIEnv*, jobject, jlong, jbyteArray, jint, jint);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(JNIEnv*, jobject, jlong, jfloat);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(JNIEnv*, jobject, jlong);
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(JNIEnv*, jobject, jlong);
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(JNIEnv*, jobject, jlong);
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_resetOboeRenderer(JNIEnv*, jobject, jlong);
    
    // Android设备信息
    JNIEXPORT jstring JNICALL Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv*, jobject);
    JNIEXPORT jstring JNICALL Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv*, jobject);
}

// C++接口
namespace RyujinxNative {
    // Vulkan辅助函数
    [[nodiscard]] std::expected<VkSurfaceKHR, VkResult> create_vulkan_surface(
        ANativeWindow* window, VkInstance instance) noexcept;
    
    [[nodiscard]] std::optional<VkSurfaceKHR> create_surface_safe(
        ANativeWindow* window, VkInstance instance) noexcept;
    
    // 线程管理
    void set_rendering_thread(std::stop_token stop_token = {}) noexcept;
    bool is_rendering_thread() noexcept;
    
    // 变换计算
    ANativeWindowTransform calculate_native_transform(int32_t transform, bool is_flipped) noexcept;
    
    // 字符串转换
    [[nodiscard]] std::unique_ptr<char[]> jstring_to_utf8(JNIEnv* env, jstring str) noexcept;
    [[nodiscard]] jstring utf8_to_jstring(JNIEnv* env, const char* utf8) noexcept;
    [[nodiscard]] jstring std_string_to_jstring(JNIEnv* env, const std::string& str) noexcept;
    
    // Oboe音频管理器
    class OboeAudioManager {
    public:
        static OboeAudioManager& instance() noexcept;
        
        bool init(int sample_rate, int channel_count, int sample_format = 0) noexcept;
        void shutdown() noexcept;
        bool write_audio(std::span<const int16_t> audio_data) noexcept;
        bool write_audio_raw(std::span<const uint8_t> audio_data, int32_t sample_format) noexcept;
        void set_volume(float volume) noexcept;
        bool is_initialized() const noexcept;
        bool is_playing() const noexcept;
        int32_t get_buffered_frames() const noexcept;
        void reset() noexcept;
        
    private:
        OboeAudioManager() = default;
        std::unique_ptr<OboeAudioRenderer> renderer_;
        mutable std::shared_mutex mutex_;
    };
    
    // Android系统信息
    [[nodiscard]] std::string get_android_device_model() noexcept;
    [[nodiscard]] std::string get_android_device_brand() noexcept;
    
    // JNI环境管理
    class JNIEnvGuard {
        JavaVM* vm_;
        JNIEnv* env_;
        bool attached_;
        
    public:
        explicit JNIEnvGuard(JavaVM* vm);
        ~JNIEnvGuard();
        
        JNIEnv* operator->() const noexcept { return env_; }
        JNIEnv* get() const noexcept { return env_; }
        explicit operator bool() const noexcept { return env_ != nullptr; }
        
        JNIEnvGuard(const JNIEnvGuard&) = delete;
        JNIEnvGuard& operator=(const JNIEnvGuard&) = delete;
    };
}

// 内联辅助函数
inline void debug_break(int code) noexcept {
    if constexpr (DEBUG) {
        if (code >= 3) {
            // 调试断点实现
            #ifdef __ANDROID__
            __android_log_print(ANDROID_LOG_DEBUG, "Ryujinx", "Debug break: %d", code);
            #endif
        }
    }
}

#endif // RYUJINX_HPP
