#include "ryuijnx.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <cstdarg>
#include <sys/system_properties.h>
#include <memory>
#include <span>
#include <atomic>

// ========== 全局变量 ==========

std::atomic<long> _renderingThreadId{0};
JavaVM* _vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative;
std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;
std::atomic<bool> isInitialOrientationFlipped{true};

// 全局单例实例（使用智能指针）
static OboeRendererPtr g_singleton_renderer = nullptr;

// ========== 辅助函数 ==========

/**
 * @brief 从jstring安全转换为std::string
 */
std::string getStringFromJString(JNIEnv* env, jstring jStr) {
    if (UNLIKELY(!jStr)) return {};
    
    ScopedJString scopedStr(env, jStr);
    if (scopedStr) {
        return std::string(scopedStr.view());
    }
    return {};
}

/**
 * @brief 从std::string创建jstring
 */
jstring createJString(JNIEnv* env, const std::string& str) {
    return env->NewStringUTF(str.c_str());
}

/**
 * @brief 编译期计算Native Window变换
 */
[[nodiscard]] constexpr ANativeWindowTransform CalculateTransform(int32_t transform, bool isFlipped) noexcept {
    transform >>= 1; // 移除最低位
    
    switch (transform) {
        case 0x1: return ANATIVEWINDOW_TRANSFORM_IDENTITY;
        case 0x2: return ANATIVEWINDOW_TRANSFORM_ROTATE_90;
        case 0x4: return isFlipped ? ANATIVEWINDOW_TRANSFORM_IDENTITY 
                                   : ANATIVEWINDOW_TRANSFORM_ROTATE_180;
        case 0x8: return ANATIVEWINDOW_TRANSFORM_ROTATE_270;
        case 0x10: return ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL;
        case 0x20: return static_cast<ANativeWindowTransform>(
            ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90);
        case 0x40: return ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
        case 0x80: return static_cast<ANativeWindowTransform>(
            ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90);
        default: return ANATIVEWINDOW_TRANSFORM_IDENTITY;
    }
}

// ========== JNI函数实现 ==========

extern "C" {

JNIEXPORT jlong JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
    JNIEnv* env, jobject instance, jobject surface) 
{
    auto* nativeWindow = ANativeWindow_fromSurface(env, surface);
    return nativeWindow ? reinterpret_cast<jlong>(nativeWindow) : -1;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
    JNIEnv* env, jobject instance, jlong window) 
{
    auto* nativeWindow = reinterpret_cast<ANativeWindow*>(window);
    if (LIKELY(nativeWindow)) {
        ANativeWindow_release(nativeWindow);
    }
}

long createSurface(long native_surface, long instance) {
    auto* nativeWindow = reinterpret_cast<ANativeWindow*>(native_surface);
    VkSurfaceKHR surface;
    auto* vkInstance = reinterpret_cast<VkInstance>(instance);
    
    auto fpCreateAndroidSurfaceKHR = reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
        vkGetInstanceProcAddr(vkInstance, "vkCreateAndroidSurfaceKHR"));
    
    if (UNLIKELY(!fpCreateAndroidSurfaceKHR)) {
        return -1;
    }
    
    VkAndroidSurfaceCreateInfoKHR info = {
        .sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR,
        .window = nativeWindow
    };
    
    VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
    return reinterpret_cast<long>(surface);
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
    JNIEnv* env, jobject instance) 
{
    return reinterpret_cast<jlong>(createSurface);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(
    JNIEnv* env, jobject thiz) 
{
    env->GetJavaVM(&_vm);
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

JNIEXPORT void JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_setCurrentTransform(
    JNIEnv* env, jobject thiz, jlong native_window, jint transform) 
{
    if (UNLIKELY(native_window == 0 || native_window == -1)) return;
    
    auto* nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    auto nativeTransform = CalculateTransform(transform, isInitialOrientationFlipped.load());
    
    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM, 
                         static_cast<int32_t>(nativeTransform));
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(
    JNIEnv* env, jobject thiz,
    jstring native_lib_path,
    jstring private_apps_path,
    jstring driver_name) 
{
    // 使用RAII包装器避免内存泄漏
    ScopedJString libPath(env, native_lib_path);
    ScopedJString privatePath(env, private_apps_path);
    ScopedJString driverName(env, driver_name);
    
    if (UNLIKELY(!libPath || !privatePath || !driverName)) {
        return -1;
    }
    
    auto handle = adrenotools_open_libvulkan(
        RTLD_NOW, ADRENOTOOLS_DRIVER_CUSTOM, nullptr,
        libPath.c_str(), privatePath.c_str(), driverName.c_str(), 
        nullptr, nullptr);
    
    return reinterpret_cast<jlong>(handle);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(
    JNIEnv* env, jobject thiz, jboolean enable) 
{
    adrenotools_set_turbo(enable);
}

JNIEXPORT jint JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(
    JNIEnv* env, jobject thiz, jlong native_window) 
{
    auto* nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    return LIKELY(nativeWindow) ? nativeWindow->maxSwapInterval : 0;
}

JNIEXPORT jint JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(
    JNIEnv* env, jobject thiz, jlong native_window) 
{
    auto* nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    return LIKELY(nativeWindow) ? nativeWindow->minSwapInterval : 0;
}

JNIEXPORT jint JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(
    JNIEnv* env, jobject thiz, jlong native_window, jint swap_interval) 
{
    auto* nativeWindow = reinterpret_cast<ANativeWindow*>(native_window);
    return LIKELY(nativeWindow) ? nativeWindow->setSwapInterval(nativeWindow, swap_interval) : -1;
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(
    JNIEnv* env, jobject thiz, jlong ptr) 
{
    return createJString(env, reinterpret_cast<char*>(ptr));
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(
    JNIEnv* env, jobject thiz, jboolean is_flipped) 
{
    isInitialOrientationFlipped.store(is_flipped);
}

// ========== 音频单例接口 ==========

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(
    JNIEnv* env, jobject thiz, jint sample_rate, jint channel_count) 
{
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<OboeAudioRenderer>();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(
    JNIEnv* env, jobject thiz, jint sample_rate, jint channel_count, jint sample_format) 
{
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<OboeAudioRenderer>();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) 
           ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(
    JNIEnv* env, jobject thiz) 
{
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        g_singleton_renderer.reset();
    }
}

JNIEXPORT jboolean JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(
    JNIEnv* env, jobject thiz, jshortArray audio_data, jint num_frames) 
{
    if (UNLIKELY(!g_singleton_renderer || !audio_data || num_frames <= 0)) {
        return JNI_FALSE;
    }
    
    ScopedJArray<jshort> scopedArray(env, audio_data);
    if (!scopedArray) return JNI_FALSE;
    
    // 使用span进行边界安全的访问
    auto audioSpan = scopedArray.span(num_frames);
    if (audioSpan.size() < static_cast<size_t>(num_frames)) {
        return JNI_FALSE;
    }
    
    bool success = g_singleton_renderer->WriteAudio(
        reinterpret_cast<const int16_t*>(audioSpan.data()), num_frames);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(
    JNIEnv* env, jobject thiz, jbyteArray audio_data, jint num_frames, jint sample_format) 
{
    if (UNLIKELY(!g_singleton_renderer || !audio_data || num_frames <= 0)) {
        return JNI_FALSE;
    }
    
    ScopedJArray<jbyte> scopedArray(env, audio_data);
    if (!scopedArray) return JNI_FALSE;
    
    auto audioSpan = scopedArray.span(num_frames);
    if (audioSpan.size() < static_cast<size_t>(num_frames)) {
        return JNI_FALSE;
    }
    
    bool success = g_singleton_renderer->WriteAudioRaw(
        reinterpret_cast<const uint8_t*>(audioSpan.data()), num_frames, sample_format);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(
    JNIEnv* env, jobject thiz, jfloat volume) 
{
    if (g_singleton_renderer) {
        g_singleton_renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(
    JNIEnv* env, jobject thiz) 
{
    return g_singleton_renderer && g_singleton_renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(
    JNIEnv* env, jobject thiz) 
{
    return g_singleton_renderer && g_singleton_renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(
    JNIEnv* env, jobject thiz) 
{
    return g_singleton_renderer ? static_cast<jint>(g_singleton_renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(
    JNIEnv* env, jobject thiz) 
{
    if (g_singleton_renderer) {
        g_singleton_renderer->Reset();
    }
}

// ========== 音频多实例接口 ==========

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createOboeRenderer(
    JNIEnv* env, jobject thiz) 
{
    return reinterpret_cast<jlong>(new OboeAudioRenderer());
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyOboeRenderer(
    JNIEnv* env, jobject thiz, jlong renderer_ptr) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
        delete renderer;
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(
    JNIEnv* env, jobject thiz, jlong renderer_ptr, jint sample_rate, jint channel_count, jint sample_format) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) 
           ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(
    JNIEnv* env, jobject thiz, jlong renderer_ptr) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
    }
}

JNIEXPORT jboolean JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(
    JNIEnv* env, jobject thiz, jlong renderer_ptr, jshortArray audio_data, jint num_frames) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    if (UNLIKELY(!renderer || !audio_data || num_frames <= 0)) {
        return JNI_FALSE;
    }
    
    ScopedJArray<jshort> scopedArray(env, audio_data);
    if (!scopedArray) return JNI_FALSE;
    
    auto audioSpan = scopedArray.span(num_frames);
    if (audioSpan.size() < static_cast<size_t>(num_frames)) {
        return JNI_FALSE;
    }
    
    bool success = renderer->WriteAudio(
        reinterpret_cast<const int16_t*>(audioSpan.data()), num_frames);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(
    JNIEnv* env, jobject thiz, jlong renderer_ptr, jbyteArray audio_data, jint num_frames, jint sample_format) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    if (UNLIKELY(!renderer || !audio_data || num_frames <= 0)) {
        return JNI_FALSE;
    }
    
    ScopedJArray<jbyte> scopedArray(env, audio_data);
    if (!scopedArray) return JNI_FALSE;
    
    auto audioSpan = scopedArray.span(num_frames);
    if (audioSpan.size() < static_cast<size_t>(num_frames)) {
        return JNI_FALSE;
    }
    
    bool success = renderer->WriteAudioRaw(
        reinterpret_cast<const uint8_t*>(audioSpan.data()), num_frames, sample_format);
    
    return success ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(
    JNIEnv* env, jobject thiz, jlong renderer_ptr, jfloat volume) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(
    JNIEnv* env, jobject thiz, jlong renderer_ptr) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(
    JNIEnv* env, jobject thiz, jlong renderer_ptr) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL HOT_PATH
Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(
    JNIEnv* env, jobject thiz, jlong renderer_ptr) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    return renderer ? static_cast<jint>(renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeRenderer(
    JNIEnv* env, jobject thiz, jlong renderer_ptr) 
{
    auto* renderer = reinterpret_cast<OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Reset();
    }
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(
    JNIEnv* env, jobject thiz) 
{
    static thread_local char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
    }
    return env->NewStringUTF(model);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(
    JNIEnv* env, jobject thiz) 
{
    static thread_local char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
    }
    return env->NewStringUTF(brand);
}

// ========== 生命周期函数 ==========

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) { 
    _vm = vm;
    return JNI_VERSION_1_6; 
}

JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    // 清理资源
    shutdownOboeAudio();
    _vm = nullptr;
    _mainActivity = nullptr;
    _mainActivityClass = nullptr;
}

} // extern "C"

// ========== 辅助函数实现 ==========

void setRenderingThread() {
    _renderingThreadIdNative = pthread_self();
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

void debug_break(int code) {
    if (code >= 3) { 
        // 调试断点
        __builtin_trap();
    }
}

// ========== C接口实现（向后兼容） ==========

bool initOboeAudio(int sample_rate, int channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<OboeAudioRenderer>();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count);
}

bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = std::make_unique<OboeAudioRenderer>();
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

void* createOboeRenderer() {
    return new OboeAudioRenderer();
}

void destroyOboeRenderer(void* renderer) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
        delete oboe_renderer;
    }
}

bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeRenderer(void* renderer) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
    }
}

bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    return oboe_renderer && data && num_frames > 0 && oboe_renderer->WriteAudio(data, num_frames);
}

bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, int32_t sample_format) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    return oboe_renderer && data && num_frames > 0 && 
           oboe_renderer->WriteAudioRaw(data, num_frames, sample_format);
}

void setOboeRendererVolume(void* renderer, float volume) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->SetVolume(volume);
    }
}

bool isOboeRendererInitialized(void* renderer) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsInitialized();
}

bool isOboeRendererPlaying(void* renderer) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsPlaying();
}

int32_t getOboeRendererBufferedFrames(void* renderer) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    return oboe_renderer ? static_cast<int32_t>(oboe_renderer->GetBufferedFrames()) : 0;
}

void resetOboeRenderer(void* renderer) {
    auto* oboe_renderer = reinterpret_cast<OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Reset();
    }
}

const char* GetAndroidDeviceModel() {
    static thread_local char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
    }
    return model;
}

const char* GetAndroidDeviceBrand() {
    static thread_local char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
    }
    return brand;
}
