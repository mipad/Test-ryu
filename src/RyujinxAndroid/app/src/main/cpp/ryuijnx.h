#ifndef RYUJINXNATIVE_RYUIJNX_H
#define RYUJINXNATIVE_RYUIJNX_H

#include <cstdlib>
#include <dlfcn.h>
#include <cstring>
#include <string>
#include <jni.h>
#include <exception>
#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include "vulkan_wrapper.h"
#include <vulkan/vulkan_android.h>
#include <cassert>
#include <fcntl.h>
#include "adrenotools/driver.h"
#include "native_window.h"
#include <pthread.h>
#include <memory>
#include <span>

#define CALL_VK(func) if (VK_SUCCESS != (func)) { assert(false); }
#define VK_CHECK(x) CALL_VK(x)
#define LoadLib(a) dlopen(a, RTLD_NOW)

// 现代化类型别名
using JStringRef = jstring;
using JByteArrayRef = jbyteArray;
using JShortArrayRef = jshortArray;

// 全局状态变量
extern std::atomic<long> _renderingThreadId;
extern JavaVM* _vm;
extern jobject _mainActivity;
extern jclass _mainActivityClass;
extern pthread_t _renderingThreadIdNative;

// Oboe渲染器智能指针类型
using OboeRendererPtr = std::unique_ptr<class OboeAudioRenderer>;

extern "C" {
    // ========== 核心接口 ==========
    
    // 单例音频接口（向后兼容）
    [[nodiscard]] bool initOboeAudio(int sample_rate, int channel_count);
    [[nodiscard]] bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format);
    void shutdownOboeAudio();
    [[nodiscard]] bool writeOboeAudio(const int16_t* data, int32_t num_frames);
    [[nodiscard]] bool writeOboeAudioRaw(const uint8_t* data, int32_t num_frames, int32_t sample_format);
    void setOboeVolume(float volume);
    [[nodiscard]] bool isOboeInitialized();
    [[nodiscard]] bool isOboePlaying();
    [[nodiscard]] int32_t getOboeBufferedFrames();
    void resetOboeAudio();
    
    // 多实例音频接口
    [[nodiscard]] void* createOboeRenderer();
    void destroyOboeRenderer(void* renderer);
    [[nodiscard]] bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format);
    void shutdownOboeRenderer(void* renderer);
    [[nodiscard]] bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames);
    [[nodiscard]] bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, int32_t sample_format);
    void setOboeRendererVolume(void* renderer, float volume);
    [[nodiscard]] bool isOboeRendererInitialized(void* renderer);
    [[nodiscard]] bool isOboeRendererPlaying(void* renderer);
    [[nodiscard]] int32_t getOboeRendererBufferedFrames(void* renderer);
    void resetOboeRenderer(void* renderer);

    // 设备信息
    [[nodiscard]] const char* GetAndroidDeviceModel();
    [[nodiscard]] const char* GetAndroidDeviceBrand();

    // Vulkan和渲染接口
    void setRenderingThread();
    void setCurrentTransform(long native_window, int transform);
    [[noreturn]] void debug_break(int code);
    
    // 字符串处理（现代化版本）
    [[nodiscard]] std::string getStringFromJString(JNIEnv* env, jstring jStr);
    [[nodiscard]] jstring createJString(JNIEnv* env, const std::string& str);
    [[nodiscard]] long createSurface(long native_surface, long instance);
}

// ========== RAII包装器 ==========

/**
 * @brief JNI字符串自动管理包装器
 */
class ScopedJString {
private:
    JNIEnv* m_env;
    jstring m_jstr;
    const char* m_cstr;
    
public:
    ScopedJString(JNIEnv* env, jstring jstr) noexcept 
        : m_env(env), m_jstr(jstr), 
          m_cstr(jstr ? env->GetStringUTFChars(jstr, nullptr) : nullptr) {}
    
    ~ScopedJString() {
        if (m_cstr && m_jstr) {
            m_env->ReleaseStringUTFChars(m_jstr, m_cstr);
        }
    }
    
    // 删除拷贝构造和赋值
    ScopedJString(const ScopedJString&) = delete;
    ScopedJString& operator=(const ScopedJString&) = delete;
    
    // 允许移动
    ScopedJString(ScopedJString&& other) noexcept 
        : m_env(other.m_env), m_jstr(other.m_jstr), m_cstr(other.m_cstr) {
        other.m_jstr = nullptr;
        other.m_cstr = nullptr;
    }
    
    // 访问器
    [[nodiscard]] const char* c_str() const noexcept { return m_cstr; }
    [[nodiscard]] explicit operator bool() const noexcept { return m_cstr != nullptr; }
    
    // 转换为std::string_view（零拷贝）
    [[nodiscard]] std::string_view view() const noexcept {
        return m_cstr ? std::string_view(m_cstr) : std::string_view();
    }
};

/**
 * @brief JNI数组自动管理包装器（模板化）
 */
template<typename T>
class ScopedJArray {
private:
    JNIEnv* m_env;
    jarray m_jarray;
    T* m_data;
    
    // 类型特化的获取函数
    T* getArrayElements(jarray arr, jboolean* isCopy) {
        if constexpr (std::is_same_v<T, jbyte>) {
            return m_env->GetByteArrayElements(static_cast<jbyteArray>(arr), isCopy);
        } else if constexpr (std::is_same_v<T, jshort>) {
            return m_env->GetShortArrayElements(static_cast<jshortArray>(arr), isCopy);
        } else if constexpr (std::is_same_v<T, jint>) {
            return m_env->GetIntArrayElements(static_cast<jintArray>(arr), isCopy);
        } else if constexpr (std::is_same_v<T, jlong>) {
            return m_env->GetLongArrayElements(static_cast<jlongArray>(arr), isCopy);
        } else if constexpr (std::is_same_v<T, jfloat>) {
            return m_env->GetFloatArrayElements(static_cast<jfloatArray>(arr), isCopy);
        } else if constexpr (std::is_same_v<T, jdouble>) {
            return m_env->GetDoubleArrayElements(static_cast<jdoubleArray>(arr), isCopy);
        } else {
            static_assert(sizeof(T) == 0, "Unsupported JNI array type");
            return nullptr;
        }
    }
    
    void releaseArrayElements(jarray arr, T* data, jint mode) {
        if constexpr (std::is_same_v<T, jbyte>) {
            m_env->ReleaseByteArrayElements(static_cast<jbyteArray>(arr), data, mode);
        } else if constexpr (std::is_same_v<T, jshort>) {
            m_env->ReleaseShortArrayElements(static_cast<jshortArray>(arr), data, mode);
        } else if constexpr (std::is_same_v<T, jint>) {
            m_env->ReleaseIntArrayElements(static_cast<jintArray>(arr), data, mode);
        } else if constexpr (std::is_same_v<T, jlong>) {
            m_env->ReleaseLongArrayElements(static_cast<jlongArray>(arr), data, mode);
        } else if constexpr (std::is_same_v<T, jfloat>) {
            m_env->ReleaseFloatArrayElements(static_cast<jfloatArray>(arr), data, mode);
        } else if constexpr (std::is_same_v<T, jdouble>) {
            m_env->ReleaseDoubleArrayElements(static_cast<jdoubleArray>(arr), data, mode);
        }
    }
    
public:
    ScopedJArray(JNIEnv* env, jarray jarr) noexcept 
        : m_env(env), m_jarray(jarr), m_data(getArrayElements(jarr, nullptr)) {}
    
    ~ScopedJArray() {
        if (m_data && m_jarray) {
            releaseArrayElements(m_jarray, m_data, JNI_ABORT);
        }
    }
    
    // 删除拷贝构造和赋值
    ScopedJArray(const ScopedJArray&) = delete;
    ScopedJArray& operator=(const ScopedJArray&) = delete;
    
    // 允许移动
    ScopedJArray(ScopedJArray&& other) noexcept 
        : m_env(other.m_env), m_jarray(other.m_jarray), m_data(other.m_data) {
        other.m_jarray = nullptr;
        other.m_data = nullptr;
    }
    
    // 访问器
    [[nodiscard]] T* data() const noexcept { return m_data; }
    [[nodiscard]] explicit operator bool() const noexcept { return m_data != nullptr; }
    
    // 转换为std::span（零拷贝）
    template<size_t Extent = std::dynamic_extent>
    [[nodiscard]] std::span<T, Extent> span(size_t length) const noexcept {
        return m_data ? std::span<T, Extent>(m_data, length) : std::span<T, Extent>();
    }
};

// ========== 性能优化宏 ==========

// 热路径函数内联提示
#if defined(__GNUC__) || defined(__clang__)
    #define HOT_PATH __attribute__((hot))
    #define COLD_PATH __attribute__((cold))
    #define ALWAYS_INLINE __attribute__((always_inline))
    #define NOINLINE __attribute__((noinline))
#elif defined(_MSC_VER)
    #define HOT_PATH __declspec(noinline)  // MSVC热路径优化不同
    #define COLD_PATH 
    #define ALWAYS_INLINE __forceinline
    #define NOINLINE __declspec(noinline)
#else
    #define HOT_PATH
    #define COLD_PATH
    #define ALWAYS_INLINE inline
    #define NOINLINE
#endif

// 分支预测优化
#if defined(__GNUC__) || defined(__clang__)
    #define LIKELY(x) __builtin_expect(!!(x), 1)
    #define UNLIKELY(x) __builtin_expect(!!(x), 0)
#else
    #define LIKELY(x) (x)
    #define UNLIKELY(x) (x)
#endif

// 缓存行对齐
#define CACHE_LINE_SIZE 64
#define ALIGNAS_CACHE_LINE alignas(CACHE_LINE_SIZE)

#endif // RYUJINXNATIVE_RYUIJNX_H
