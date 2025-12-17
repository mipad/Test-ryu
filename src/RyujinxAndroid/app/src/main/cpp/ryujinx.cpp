#include "ryujinx.hpp"
#include "oboe_audio_renderer.h"
#include <chrono>
#include <csignal>
#include <sys/system_properties.h>
#include <algorithm>
#include <execution>
#include <bit>
#include <format>
#include <print>
#include <source_location>

// Oboe音频渲染器实现
namespace RyujinxOboe {
    class OboeAudioRenderer {
    public:
        OboeAudioRenderer() = default;
        ~OboeAudioRenderer() { Shutdown(); }
        
        bool Initialize(int sample_rate, int channel_count) noexcept {
            std::lock_guard lock(mutex_);
            // 实现初始化逻辑
            initialized_ = true;
            return true;
        }
        
        bool InitializeWithFormat(int sample_rate, int channel_count, int sample_format) noexcept {
            std::lock_guard lock(mutex_);
            // 实现带格式的初始化
            initialized_ = true;
            sample_format_ = sample_format;
            return true;
        }
        
        void Shutdown() noexcept {
            std::lock_guard lock(mutex_);
            initialized_ = false;
            playing_ = false;
        }
        
        bool WriteAudio(const int16_t* data, int32_t num_frames) noexcept {
            if (!initialized_ || !data || num_frames <= 0) return false;
            
            std::lock_guard lock(mutex_);
            // 实现音频写入逻辑
            buffered_frames_ += num_frames;
            return true;
        }
        
        bool WriteAudioRaw(const uint8_t* data, int32_t num_frames, int32_t sample_format) noexcept {
            if (!initialized_ || !data || num_frames <= 0) return false;
            
            std::lock_guard lock(mutex_);
            // 实现原始音频写入
            buffered_frames_ += num_frames;
            return true;
        }
        
        bool WriteAudioSpan(std::span<const int16_t> audio_data) noexcept {
            return WriteAudio(audio_data.data(), static_cast<int32_t>(audio_data.size()));
        }
        
        bool WriteAudioSpanRaw(std::span<const uint8_t> audio_data, int32_t sample_format) noexcept {
            return WriteAudioRaw(audio_data.data(), static_cast<int32_t>(audio_data.size()), sample_format);
        }
        
        void SetVolume(float volume) noexcept {
            std::lock_guard lock(mutex_);
            volume_ = std::clamp(volume, 0.0f, 1.0f);
        }
        
        bool IsInitialized() const noexcept { 
            std::shared_lock lock(mutex_);
            return initialized_; 
        }
        
        bool IsPlaying() const noexcept { 
            std::shared_lock lock(mutex_);
            return playing_; 
        }
        
        int32_t GetBufferedFrames() const noexcept { 
            std::shared_lock lock(mutex_);
            return buffered_frames_; 
        }
        
        void Reset() noexcept {
            std::lock_guard lock(mutex_);
            buffered_frames_ = 0;
        }
        
    private:
        mutable std::shared_mutex mutex_;
        bool initialized_ = false;
        bool playing_ = false;
        float volume_ = 1.0f;
        int32_t buffered_frames_ = 0;
        int32_t sample_format_ = 0;
    };
}

// 全局实例
static std::unique_ptr<RyujinxOboe::OboeAudioRenderer> g_singleton_renderer;

// JNI函数实现
extern "C" {

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
    JNIEnv* env, jobject instance, jobject surface) noexcept
{
    auto native_window = ANativeWindow_fromSurface(env, surface);
    return native_window ? reinterpret_cast<jlong>(native_window) : -1;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
    JNIEnv* env, jobject instance, jlong window) noexcept
{
    auto native_window = reinterpret_cast<ANativeWindow*>(window);
    if (native_window) {
        ANativeWindow_release(native_window);
    }
}

// Vulkan表面创建函数
long createSurface(long native_surface, long instance) noexcept {
    auto* native_window = reinterpret_cast<ANativeWindow*>(native_surface);
    auto* vk_instance = reinterpret_cast<VkInstance>(instance);
    
    auto fp_create_android_surface = reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
        vkGetInstanceProcAddr(vk_instance, "vkCreateAndroidSurfaceKHR"));
    
    if (!fp_create_android_surface) return -1;
    
    VkAndroidSurfaceCreateInfoKHR info = {
        .sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR,
        .window = native_window
    };
    
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkResult result = fp_create_android_surface(vk_instance, &info, nullptr, &surface);
    
    if (result != VK_SUCCESS) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", 
                           "Failed to create Vulkan surface: %d", result);
        return -1;
    }
    
    return reinterpret_cast<long>(surface);
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
    JNIEnv* env, jobject instance) noexcept
{
    return reinterpret_cast<jlong>(createSurface);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setRenderingThread(
    JNIEnv* env, jobject instance) noexcept
{
    Global::rendering_thread = pthread_self();
    Global::current_time_point = std::chrono::steady_clock::now();
}

JNIEXPORT jint JNICALL 
JNI_OnLoad(JavaVM* vm, void* reserved) noexcept
{
    Global::vm = vm;
    return JNI_VERSION_1_6;
}

JNIEXPORT void JNICALL 
JNI_OnUnload(JavaVM* vm, void* reserved) noexcept
{
    Global::oboe_renderer.destroy();
    Global::vm = nullptr;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(
    JNIEnv* env, jobject thiz) noexcept
{
    JavaVM* vm = nullptr;
    env->GetJavaVM(&vm);
    Global::vm = vm;
    Global::main_activity = env->NewGlobalRef(thiz);
    Global::main_activity_class = static_cast<jclass>(env->NewGlobalRef(env->GetObjectClass(thiz)));
}

// 变换设置函数
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setCurrentTransform(
    JNIEnv* env, jobject instance, jlong native_window, jint transform) noexcept
{
    if (!native_window || native_window == -1) return;
    
    auto* native_window_ptr = reinterpret_cast<ANativeWindow*>(native_window);
    auto native_transform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
    
    // 移除最低位
    int32_t transform_val = transform >> 1;
    
    // 使用查找表提高性能
    static constexpr std::array<std::pair<int32_t, ANativeWindowTransform>, 9> transform_map = {{
        {0x1, ANATIVEWINDOW_TRANSFORM_IDENTITY},
        {0x2, ANATIVEWINDOW_TRANSFORM_ROTATE_90},
        {0x4, Global::is_initial_orientation_flipped ? 
            ANATIVEWINDOW_TRANSFORM_IDENTITY : ANATIVEWINDOW_TRANSFORM_ROTATE_180},
        {0x8, ANATIVEWINDOW_TRANSFORM_ROTATE_270},
        {0x10, ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL},
        {0x20, static_cast<ANativeWindowTransform>(
            ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90)},
        {0x40, ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL},
        {0x80, static_cast<ANativeWindowTransform>(
            ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90)},
        {0x100, ANATIVEWINDOW_TRANSFORM_IDENTITY}
    }};
    
    auto it = std::ranges::find_if(transform_map, 
        [transform_val](const auto& pair) { return pair.first == transform_val; });
    
    if (it != transform_map.end()) {
        native_transform = it->second;
    }
    
    // 调用NativeWindow的perform函数
    native_window_ptr->perform(native_window_ptr, 
        NATIVE_WINDOW_SET_BUFFERS_TRANSFORM, 
        static_cast<int32_t>(native_transform));
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(
    JNIEnv* env, jobject instance,
    jstring native_lib_path,
    jstring private_apps_path,
    jstring driver_name) noexcept
{
    JNIString lib_path(env, native_lib_path);
    JNIString private_path(env, private_apps_path);
    JNIString driver(env, driver_name);
    
    if (!lib_path || !private_path || !driver) {
        return 0;
    }
    
    auto handle = adrenotools_open_libvulkan(
        RTLD_NOW, ADRENOTOOLS_DRIVER_CUSTOM, nullptr,
        lib_path.c_str(), private_path.c_str(), driver.c_str(),
        nullptr, nullptr);
    
    return reinterpret_cast<jlong>(handle);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(
    JNIEnv* env, jobject instance, jboolean enable) noexcept
{
    adrenotools_set_turbo(enable == JNI_TRUE);
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(
    JNIEnv* env, jobject instance, jlong native_window) noexcept
{
    auto* native_window_ptr = reinterpret_cast<ANativeWindow*>(native_window);
    return native_window_ptr ? native_window_ptr->maxSwapInterval : 0;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(
    JNIEnv* env, jobject instance, jlong native_window) noexcept
{
    auto* native_window_ptr = reinterpret_cast<ANativeWindow*>(native_window);
    return native_window_ptr ? native_window_ptr->minSwapInterval : 0;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(
    JNIEnv* env, jobject instance, jlong native_window, jint swap_interval) noexcept
{
    auto* native_window_ptr = reinterpret_cast<ANativeWindow*>(native_window);
    if (!native_window_ptr) return -1;
    
    return native_window_ptr->setSwapInterval(native_window_ptr, swap_interval);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(
    JNIEnv* env, jobject instance, jlong ptr) noexcept
{
    auto* str_ptr = reinterpret_cast<const char*>(ptr);
    return str_ptr ? env->NewStringUTF(str_ptr) : nullptr;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(
    JNIEnv* env, jobject instance, jboolean is_flipped) noexcept
{
    Global::is_initial_orientation_flipped = (is_flipped == JNI_TRUE);
}

// ========== 单例Oboe音频接口 ==========

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(
    JNIEnv* env, jobject instance, jint sample_rate, jint channel_count) noexcept
{
    auto* renderer = Global::oboe_renderer.get_or_create();
    return renderer->Initialize(sample_rate, channel_count) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(
    JNIEnv* env, jobject instance, 
    jint sample_rate, jint channel_count, jint sample_format) noexcept
{
    auto* renderer = Global::oboe_renderer.get_or_create();
    return renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) 
        ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(
    JNIEnv* env, jobject instance) noexcept
{
    Global::oboe_renderer.destroy();
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(
    JNIEnv* env, jobject instance, jshortArray audio_data, jint num_frames) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    if (!renderer || !audio_data || num_frames <= 0) {
        return JNI_FALSE;
    }
    
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (!data) {
        return JNI_FALSE;
    }
    
    bool success = renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
    env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(
    JNIEnv* env, jobject instance, 
    jbyteArray audio_data, jint num_frames, jint sample_format) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    if (!renderer || !audio_data || num_frames <= 0) {
        return JNI_FALSE;
    }
    
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (!data) {
        return JNI_FALSE;
    }
    
    bool success = renderer->WriteAudioRaw(
        reinterpret_cast<uint8_t*>(data), num_frames, sample_format);
    env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(
    JNIEnv* env, jobject instance, jfloat volume) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    if (renderer) {
        renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(
    JNIEnv* env, jobject instance) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    return renderer && renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(
    JNIEnv* env, jobject instance) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    return renderer && renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(
    JNIEnv* env, jobject instance) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    return renderer ? static_cast<jint>(renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(
    JNIEnv* env, jobject instance) noexcept
{
    auto* renderer = Global::oboe_renderer.get();
    if (renderer) {
        renderer->Reset();
    }
}

// ========== 多实例Oboe音频接口 ==========

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createOboeRenderer(
    JNIEnv* env, jobject instance) noexcept
{
    try {
        auto renderer = std::make_unique<RyujinxOboe::OboeAudioRenderer>();
        return reinterpret_cast<jlong>(renderer.release());
    } catch (const std::exception& e) {
        __android_log_print(ANDROID_LOG_ERROR, "Ryujinx", 
                           "Failed to create Oboe renderer: %s", e.what());
        return 0;
    }
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyOboeRenderer(
    JNIEnv* env, jobject instance, jlong renderer_ptr) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        delete renderer;
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(
    JNIEnv* env, jobject instance, 
    jlong renderer_ptr, jint sample_rate, jint channel_count, jint sample_format) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->InitializeWithFormat(sample_rate, channel_count, sample_format)
        ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(
    JNIEnv* env, jobject instance, jlong renderer_ptr) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(
    JNIEnv* env, jobject instance, 
    jlong renderer_ptr, jshortArray audio_data, jint num_frames) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) {
        return JNI_FALSE;
    }
    
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (!data) {
        return JNI_FALSE;
    }
    
    bool success = renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
    env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(
    JNIEnv* env, jobject instance, 
    jlong renderer_ptr, jbyteArray audio_data, jint num_frames, jint sample_format) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) {
        return JNI_FALSE;
    }
    
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (!data) {
        return JNI_FALSE;
    }
    
    bool success = renderer->WriteAudioRaw(
        reinterpret_cast<uint8_t*>(data), num_frames, sample_format);
    env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(
    JNIEnv* env, jobject instance, jlong renderer_ptr, jfloat volume) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(
    JNIEnv* env, jobject instance, jlong renderer_ptr) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(
    JNIEnv* env, jobject instance, jlong renderer_ptr) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(
    JNIEnv* env, jobject instance, jlong renderer_ptr) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer ? static_cast<jint>(renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeRenderer(
    JNIEnv* env, jobject instance, jlong renderer_ptr) noexcept
{
    auto* renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Reset();
    }
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(
    JNIEnv* env, jobject instance) noexcept
{
    char model[PROP_VALUE_MAX] = {0};
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(
    JNIEnv* env, jobject instance) noexcept
{
    char brand[PROP_VALUE_MAX] = {0};
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

} // extern "C" 结束

// ========== C++原生接口实现 ==========

namespace RyujinxNative {

// Vulkan表面创建
std::expected<VkSurfaceKHR, VkResult> create_vulkan_surface(
    ANativeWindow* window, VkInstance instance) noexcept
{
    if (!window || !instance) {
        return std::unexpected(VK_ERROR_INITIALIZATION_FAILED);
    }
    
    auto fp_create_android_surface = reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
        vkGetInstanceProcAddr(instance, "vkCreateAndroidSurfaceKHR"));
    
    if (!fp_create_android_surface) {
        return std::unexpected(VK_ERROR_EXTENSION_NOT_PRESENT);
    }
    
    VkAndroidSurfaceCreateInfoKHR info = {
        .sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR,
        .window = window
    };
    
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkResult result = fp_create_android_surface(instance, &info, nullptr, &surface);
    
    if (result != VK_SUCCESS) {
        return std::unexpected(result);
    }
    
    return surface;
}

std::optional<VkSurfaceKHR> create_surface_safe(
    ANativeWindow* window, VkInstance instance) noexcept
{
    auto result = create_vulkan_surface(window, instance);
    if (result) {
        return *result;
    }
    return std::nullopt;
}

// 线程管理
void set_rendering_thread(std::stop_token stop_token) noexcept {
    Global::rendering_thread = pthread_self();
    Global::current_time_point = std::chrono::steady_clock::now();
    
    // 可以添加线程特定的初始化
    if constexpr (DEBUG) {
        __android_log_print(ANDROID_LOG_INFO, "Ryujinx", 
                           "Rendering thread set: %lu", 
                           static_cast<unsigned long>(pthread_self()));
    }
}

bool is_rendering_thread() noexcept {
    return pthread_equal(pthread_self(), Global::rendering_thread) != 0;
}

// 变换计算
ANativeWindowTransform calculate_native_transform(int32_t transform, bool is_flipped) noexcept {
    // 使用编译时计算表
    static constexpr auto transform_lookup = []() constexpr {
        std::array<ANativeWindowTransform, 256> table{};
        table.fill(ANATIVEWINDOW_TRANSFORM_IDENTITY);
        
        // 预计算所有可能的变换
        for (int i = 0; i < 256; ++i) {
            int32_t val = i >> 1;
            switch (val) {
                case 0x1: table[i] = ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
                case 0x2: table[i] = ANATIVEWINDOW_TRANSFORM_ROTATE_90; break;
                case 0x4: table[i] = is_flipped ? 
                    ANATIVEWINDOW_TRANSFORM_IDENTITY : ANATIVEWINDOW_TRANSFORM_ROTATE_180; break;
                case 0x8: table[i] = ANATIVEWINDOW_TRANSFORM_ROTATE_270; break;
                case 0x10: table[i] = ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL; break;
                case 0x20: table[i] = static_cast<ANativeWindowTransform>(
                    ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
                case 0x40: table[i] = ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL; break;
                case 0x80: table[i] = static_cast<ANativeWindowTransform>(
                    ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
                case 0x100: table[i] = ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
            }
        }
        return table;
    }();
    
    if (transform >= 0 && transform < static_cast<int32_t>(transform_lookup.size())) {
        return transform_lookup[transform];
    }
    
    return ANATIVEWINDOW_TRANSFORM_IDENTITY;
}

// 字符串转换
std::unique_ptr<char[]> jstring_to_utf8(JNIEnv* env, jstring str) noexcept {
    if (!env || !str) return nullptr;
    
    const char* cstr = env->GetStringUTFChars(str, nullptr);
    if (!cstr) return nullptr;
    
    jsize len = env->GetStringUTFLength(str);
    auto buffer = std::make_unique<char[]>(len + 1);
    std::strncpy(buffer.get(), cstr, len);
    buffer[len] = '\0';
    
    env->ReleaseStringUTFChars(str, cstr);
    return buffer;
}

jstring utf8_to_jstring(JNIEnv* env, const char* utf8) noexcept {
    return utf8 ? env->NewStringUTF(utf8) : nullptr;
}

jstring std_string_to_jstring(JNIEnv* env, const std::string& str) noexcept {
    return utf8_to_jstring(env, str.c_str());
}

// Oboe音频管理器
OboeAudioManager& OboeAudioManager::instance() noexcept {
    static OboeAudioManager instance;
    return instance;
}

bool OboeAudioManager::init(int sample_rate, int channel_count, int sample_format) noexcept {
    std::lock_guard lock(mutex_);
    
    if (!renderer_) {
        renderer_ = std::make_unique<RyujinxOboe::OboeAudioRenderer>();
    }
    
    if (sample_format == 0) {
        return renderer_->Initialize(sample_rate, channel_count);
    } else {
        return renderer_->InitializeWithFormat(sample_rate, channel_count, sample_format);
    }
}

void OboeAudioManager::shutdown() noexcept {
    std::lock_guard lock(mutex_);
    renderer_.reset();
}

bool OboeAudioManager::write_audio(std::span<const int16_t> audio_data) noexcept {
    std::shared_lock lock(mutex_);
    if (!renderer_) return false;
    
    return renderer_->WriteAudioSpan(audio_data);
}

bool OboeAudioManager::write_audio_raw(std::span<const uint8_t> audio_data, int32_t sample_format) noexcept {
    std::shared_lock lock(mutex_);
    if (!renderer_) return false;
    
    return renderer_->WriteAudioSpanRaw(audio_data, sample_format);
}

void OboeAudioManager::set_volume(float volume) noexcept {
    std::shared_lock lock(mutex_);
    if (renderer_) {
        renderer_->SetVolume(volume);
    }
}

bool OboeAudioManager::is_initialized() const noexcept {
    std::shared_lock lock(mutex_);
    return renderer_ && renderer_->IsInitialized();
}

bool OboeAudioManager::is_playing() const noexcept {
    std::shared_lock lock(mutex_);
    return renderer_ && renderer_->IsPlaying();
}

int32_t OboeAudioManager::get_buffered_frames() const noexcept {
    std::shared_lock lock(mutex_);
    return renderer_ ? renderer_->GetBufferedFrames() : 0;
}

void OboeAudioManager::reset() noexcept {
    std::shared_lock lock(mutex_);
    if (renderer_) {
        renderer_->Reset();
    }
}

// Android系统信息
std::string get_android_device_model() noexcept {
    char model[PROP_VALUE_MAX] = {0};
    __system_property_get("ro.product.model", model);
    return std::string(model);
}

std::string get_android_device_brand() noexcept {
    char brand[PROP_VALUE_MAX] = {0};
    __system_property_get("ro.product.brand", brand);
    return std::string(brand);
}

// JNI环境管理
JNIEnvGuard::JNIEnvGuard(JavaVM* vm) : vm_(vm), env_(nullptr), attached_(false) {
    if (!vm_) return;
    
    jint result = vm_->GetEnv(reinterpret_cast<void**>(&env_), JNI_VERSION_1_6);
    if (result == JNI_EDETACHED) {
        result = vm_->AttachCurrentThread(&env_, nullptr);
        if (result == JNI_OK) {
            attached_ = true;
        } else {
            env_ = nullptr;
        }
    }
}

JNIEnvGuard::~JNIEnvGuard() {
    if (vm_ && attached_ && env_) {
        vm_->DetachCurrentThread();
    }
}

} // namespace RyujinxNative

// ========== C接口实现（向后兼容） ==========

// 单例Oboe音频C接口
bool initOboeAudio(int sample_rate, int channel_count) noexcept {
    return RyujinxNative::OboeAudioManager::instance().init(sample_rate, channel_count);
}

bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format) noexcept {
    return RyujinxNative::OboeAudioManager::instance().init(sample_rate, channel_count, sample_format);
}

void shutdownOboeAudio() noexcept {
    RyujinxNative::OboeAudioManager::instance().shutdown();
}

bool writeOboeAudio(const int16_t* data, int32_t num_frames) noexcept {
    if (!data || num_frames <= 0) return false;
    
    return RyujinxNative::OboeAudioManager::instance().write_audio(
        std::span<const int16_t>(data, num_frames));
}

bool writeOboeAudioRaw(const uint8_t* data, int32_t num_frames, int32_t sample_format) noexcept {
    if (!data || num_frames <= 0) return false;
    
    return RyujinxNative::OboeAudioManager::instance().write_audio_raw(
        std::span<const uint8_t>(data, num_frames), sample_format);
}

void setOboeVolume(float volume) noexcept {
    RyujinxNative::OboeAudioManager::instance().set_volume(volume);
}

bool isOboeInitialized() noexcept {
    return RyujinxNative::OboeAudioManager::instance().is_initialized();
}

bool isOboePlaying() noexcept {
    return RyujinxNative::OboeAudioManager::instance().is_playing();
}

int32_t getOboeBufferedFrames() noexcept {
    return RyujinxNative::OboeAudioManager::instance().get_buffered_frames();
}

void resetOboeAudio() noexcept {
    RyujinxNative::OboeAudioManager::instance().reset();
}

// 多实例Oboe音频C接口
void* createOboeRenderer() noexcept {
    try {
        return new RyujinxOboe::OboeAudioRenderer();
    } catch (...) {
        return nullptr;
    }
}

void destroyOboeRenderer(void* renderer) noexcept {
    delete reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
}

bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (!oboe_renderer) return false;
    
    if (sample_format == 0) {
        return oboe_renderer->Initialize(sample_rate, channel_count);
    } else {
        return oboe_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
    }
}

void shutdownOboeRenderer(void* renderer) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
    }
}

bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (!oboe_renderer || !data || num_frames <= 0) return false;
    
    return oboe_renderer->WriteAudio(data, num_frames);
}

bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, int32_t sample_format) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (!oboe_renderer || !data || num_frames <= 0) return false;
    
    return oboe_renderer->WriteAudioRaw(data, num_frames, sample_format);
}

void setOboeRendererVolume(void* renderer, float volume) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->SetVolume(volume);
    }
}

bool isOboeRendererInitialized(void* renderer) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsInitialized();
}

bool isOboeRendererPlaying(void* renderer) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsPlaying();
}

int32_t getOboeRendererBufferedFrames(void* renderer) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer ? oboe_renderer->GetBufferedFrames() : 0;
}

void resetOboeRenderer(void* renderer) noexcept {
    auto* oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Reset();
    }
}

const char* GetAndroidDeviceModel() noexcept {
    static std::string model_cache;
    if (model_cache.empty()) {
        model_cache = RyujinxNative::get_android_device_model();
    }
    return model_cache.c_str();
}

const char* GetAndroidDeviceBrand() noexcept {
    static std::string brand_cache;
    if (brand_cache.empty()) {
        brand_cache = RyujinxNative::get_android_device_brand();
    }
    return brand_cache.c_str();
}
