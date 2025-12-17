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
#include <memory>  // C++20: 智能指针支持

#define CALL_VK(func) if (VK_SUCCESS != (func)) { assert(false); }
#define VK_CHECK(x) CALL_VK(x)
#define LoadLib(a) dlopen(a, RTLD_NOW)

// C++20: 使用constexpr提高编译时优化
constexpr int MAX_DEVICE_INFO_LENGTH = 128;

void *_ryujinxNative = NULL;
bool (*initialize)(char *) = NULL;

extern long _renderingThreadId;
extern JavaVM *_vm;
extern jobject _mainActivity;
extern jclass _mainActivityClass;
extern pthread_t _renderingThreadIdNative;

// C++20: 添加nodiscard属性防止忽略返回值
[[nodiscard]] char *getStringPointer(JNIEnv *env, jstring jS);
[[nodiscard]] jstring createString(JNIEnv *env, char *ch);
[[nodiscard]] jstring createStringFromStdString(JNIEnv *env, std::string s);
[[nodiscard]] long createSurface(long native_surface, long instance);

extern "C" {
    // 单例接口 (保持向后兼容)
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
    
    // 多实例接口 - 使用void*但内部使用智能指针
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

    [[nodiscard]] const char* GetAndroidDeviceModel();
    [[nodiscard]] const char* GetAndroidDeviceBrand();

    void setRenderingThread();
    void setCurrentTransform(long native_window, int transform);
    void debug_break(int code);
}

#endif //RYUJINXNATIVE_RYUIJNX_H
