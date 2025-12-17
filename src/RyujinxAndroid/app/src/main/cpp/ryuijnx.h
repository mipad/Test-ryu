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
extern long _renderingThreadId;
extern JavaVM* _vm;
extern jobject _mainActivity;
extern jclass _mainActivityClass;
extern pthread_t _renderingThreadIdNative;

// 前向声明
class OboeAudioRenderer;
using OboeRendererPtr = OboeAudioRenderer*;

extern "C" {
    // ========== 核心JNI接口 ==========
    
    // Vulkan和渲染接口
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
        JNIEnv* env, jobject instance, jobject surface);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
        JNIEnv* env, jobject instance, jlong window);
    
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
        JNIEnv* env, jobject instance);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_MainActivity_initVm(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setCurrentTransform(
        JNIEnv* env, jobject thiz, jlong native_window, jint transform);
    
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_loadDriver(
        JNIEnv* env, jobject thiz,
        jstring native_lib_path,
        jstring private_apps_path,
        jstring driver_name);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setTurboMode(
        JNIEnv* env, jobject thiz, jboolean enable);
    
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(
        JNIEnv* env, jobject thiz, jlong native_window);
    
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(
        JNIEnv* env, jobject thiz, jlong native_window);
    
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_setSwapInterval(
        JNIEnv* env, jobject thiz, jlong native_window, jint swap_interval);
    
    JNIEXPORT jstring JNICALL Java_org_ryujinx_android_NativeHelpers_getStringJava(
        JNIEnv* env, jobject thiz, jlong ptr);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(
        JNIEnv* env, jobject thiz, jboolean is_flipped);
    
    // 音频单例接口
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_initOboeAudio(
        JNIEnv* env, jobject thiz, jint sample_rate, jint channel_count);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(
        JNIEnv* env, jobject thiz, jint sample_rate, jint channel_count, jint sample_format);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(
        JNIEnv* env, jobject thiz, jshortArray audio_data, jint num_frames);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(
        JNIEnv* env, jobject thiz, jbyteArray audio_data, jint num_frames, jint sample_format);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setOboeVolume(
        JNIEnv* env, jobject thiz, jfloat volume);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboePlaying(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(
        JNIEnv* env, jobject thiz);
    
    // 音频多实例接口
    JNIEXPORT jlong JNICALL Java_org_ryujinx_android_NativeHelpers_createOboeRenderer(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_destroyOboeRenderer(
        JNIEnv* env, jobject thiz, jlong renderer_ptr);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(
        JNIEnv* env, jobject thiz, jlong renderer_ptr, jint sample_rate, jint channel_count, jint sample_format);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(
        JNIEnv* env, jobject thiz, jlong renderer_ptr);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(
        JNIEnv* env, jobject thiz, jlong renderer_ptr, jshortArray audio_data, jint num_frames);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(
        JNIEnv* env, jobject thiz, jlong renderer_ptr, jbyteArray audio_data, jint num_frames, jint sample_format);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(
        JNIEnv* env, jobject thiz, jlong renderer_ptr, jfloat volume);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(
        JNIEnv* env, jobject thiz, jlong renderer_ptr);
    
    JNIEXPORT jboolean JNICALL Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(
        JNIEnv* env, jobject thiz, jlong renderer_ptr);
    
    JNIEXPORT jint JNICALL Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(
        JNIEnv* env, jobject thiz, jlong renderer_ptr);
    
    JNIEXPORT void JNICALL Java_org_ryujinx_android_NativeHelpers_resetOboeRenderer(
        JNIEnv* env, jobject thiz, jlong renderer_ptr);
    
    JNIEXPORT jstring JNICALL Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(
        JNIEnv* env, jobject thiz);
    
    JNIEXPORT jstring JNICALL Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(
        JNIEnv* env, jobject thiz);
    
    // 生命周期函数
    JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved);
    JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved);
}

// ========== C++辅助函数 ==========

// 这些是C++函数，不在extern "C"中
char* getStringPointer(JNIEnv* env, jstring jS);
jstring createString(JNIEnv* env, char* ch);
jstring createStringFromStdString(JNIEnv* env, std::string s);
long createSurface(long native_surface, long instance);

// 设备信息
const char* GetAndroidDeviceModel();
const char* GetAndroidDeviceBrand();

// 渲染线程
void setRenderingThread();
void debug_break(int code);

// 音频接口（C++，不在extern "C"中）
bool initOboeAudio(int sample_rate, int channel_count);
bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format);
void shutdownOboeAudio();
bool writeOboeAudio(const int16_t* data, int32_t num_frames);
bool writeOboeAudioRaw(const uint8_t* data, int32_t num_frames, int32_t sample_format);
void setOboeVolume(float volume);
bool isOboeInitialized();
bool isOboePlaying();
int32_t getOboeBufferedFrames();
void resetOboeAudio();

// 音频多实例接口
void* createOboeRenderer();
void destroyOboeRenderer(void* renderer);
bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format);
void shutdownOboeRenderer(void* renderer);
bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames);
bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, int32_t sample_format);
void setOboeRendererVolume(void* renderer, float volume);
bool isOboeRendererInitialized(void* renderer);
bool isOboeRendererPlaying(void* renderer);
int32_t getOboeRendererBufferedFrames(void* renderer);
void resetOboeRenderer(void* renderer);

// ========== 性能优化宏 ==========

// 热路径函数内联提示
#if defined(__GNUC__) || defined(__clang__)
    #define HOT_PATH __attribute__((hot))
    #define COLD_PATH __attribute__((cold))
    #define ALWAYS_INLINE __attribute__((always_inline))
    #define NOINLINE __attribute__((noinline))
#elif defined(_MSC_VER)
    #define HOT_PATH __declspec(noinline)
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
