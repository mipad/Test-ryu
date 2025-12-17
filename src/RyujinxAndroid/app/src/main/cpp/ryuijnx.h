#ifndef RYUJINXNATIVE_RYUIJNX_H
#define RYUJINXNATIVE_RYUIJNX_H

#include <stdlib.h>
#include <dlfcn.h>
#include <string>
#include <jni.h>
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
#include <cstdint>

#define CALL_VK(func) if (VK_SUCCESS != (func)) { assert(false); }
#define VK_CHECK(x) CALL_VK(x)
#define LoadLib(a) dlopen(a, RTLD_NOW)

inline void *_ryujinxNative = nullptr;
inline bool (*initialize)(char *) = nullptr;

extern "C" {
    // 渲染线程相关
    extern long _renderingThreadId;
    extern JavaVM *_vm;
    extern jobject _mainActivity;
    extern jclass _mainActivityClass;
    extern pthread_t _renderingThreadIdNative;
    extern bool isInitialOrientationFlipped;

    // 单例音频接口 (保持向后兼容)
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
    
    // 多实例音频接口
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

    // 设备信息接口
    const char* GetAndroidDeviceModel();
    const char* GetAndroidDeviceBrand();

    // 工具函数
    void setRenderingThread();
    void setCurrentTransform(long native_window, int transform);
    void debug_break(int code);
    [[nodiscard]] char *getStringPointer(JNIEnv *env, jstring jS);
    [[nodiscard]] jstring createString(JNIEnv *env, char *ch);
    [[nodiscard]] jstring createStringFromStdString(JNIEnv *env, std::string s);
    [[nodiscard]] long createSurface(long native_surface, long instance);
}

#endif // RYUJINXNATIVE_RYUIJNX_H