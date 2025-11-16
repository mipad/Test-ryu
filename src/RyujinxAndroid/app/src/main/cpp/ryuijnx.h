//
// Created by Emmanuel Hansen on 6/19/2023.
//

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

// A macro to pass call to Vulkan and check for return value for success
#define CALL_VK(func)                                                 \
  if (VK_SUCCESS != (func)) {                                         \
    assert(false);                                                    \
  }

// A macro to check value is VK_SUCCESS
// Used also for non-vulkan functions but return VK_SUCCESS
#define VK_CHECK(x)  CALL_VK(x)

#define LoadLib(a) dlopen(a, RTLD_NOW)

void *_ryujinxNative = NULL;

// Ryujinx imported functions
bool (*initialize)(char *) = NULL;

// 全局变量声明 (在头文件中声明为extern)
extern long _renderingThreadId;
extern JavaVM *_vm;
extern jobject _mainActivity;
extern jclass _mainActivityClass;
extern pthread_t _renderingThreadIdNative;

// =============== Oboe 音频函数声明 ===============
extern "C" {
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
    
    // ARM 稳定回调控制函数
    void setOboeStabilizedCallbackEnabled(bool enabled);
    bool isOboeStabilizedCallbackEnabled();
    void setOboeStabilizedCallbackIntensity(float intensity);
    float getOboeStabilizedCallbackIntensity();
    
    // 设备信息函数
    const char* GetAndroidDeviceModel();
    const char* GetAndroidDeviceBrand();
}

// =============== 其他全局函数声明 ===============
extern "C" {
    // 渲染线程相关
    void setRenderingThread();
    
    // 窗口变换相关
    void setCurrentTransform(long native_window, int transform);
    
    // 调试相关
    void debug_break(int code);
    
    // 字符串处理函数（在 ryujinx.cpp 中定义）
    char *getStringPointer(JNIEnv *env, jstring jS);
    jstring createString(JNIEnv *env, char *ch);
    jstring createStringFromStdString(JNIEnv *env, std::string s);
    
    // 表面创建函数
    long createSurface(long native_surface, long instance);
}

#endif //RYUJINXNATIVE_RYUIJNX_H