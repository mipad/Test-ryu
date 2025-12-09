#ifndef RYUJINXNATIVE_RYUIJNX_H
#define RYUJINXNATIVE_RYUIJNX_H

#include <stdlib.h>
#include <dlfcn.h>
#include <string.h>
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

#define CALL_VK(func) if (VK_SUCCESS != (func)) { assert(false); }
#define VK_CHECK(x) CALL_VK(x)
#define LoadLib(a) dlopen(a, RTLD_NOW)

void *_ryujinxNative = NULL;
bool (*initialize)(char *) = NULL;

extern long _renderingThreadId;
extern JavaVM *_vm;
extern jobject _mainActivity;
extern jclass _mainActivityClass;
extern pthread_t _renderingThreadIdNative;

extern "C" {
    // 单例接口 (保持向后兼容)
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
    
    // 多实例接口
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

    const char* GetAndroidDeviceModel();
    const char* GetAndroidDeviceBrand();

    void setRenderingThread();
    void setCurrentTransform(long native_window, int transform);
    void debug_break(int code);
    char *getStringPointer(JNIEnv *env, jstring jS);
    jstring createString(JNIEnv *env, char *ch);
    jstring createStringFromStdString(JNIEnv *env, std::string s);
    long createSurface(long native_surface, long instance);
}

#endif //RYUJINXNATIVE_RYUIJNX_H