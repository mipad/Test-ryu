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

#define CALL_VK(func)                                                 \
  if (VK_SUCCESS != (func)) {                                         \
    __android_log_print(ANDROID_LOG_ERROR, "Tutorial ",               \
                        "Vulkan error. File[%s], line[%d]", __FILE__, \
                        __LINE__);                                    \
    assert(false);                                                    \
  }

#define VK_CHECK(x)  CALL_VK(x)
#define LoadLib(a) dlopen(a, RTLD_NOW)

void *_ryujinxNative = NULL;
bool (*initialize)(char *) = NULL;
long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;

extern "C" {
    void initOboeAudio();
    void shutdownOboeAudio();
    void writeOboeAudio(float* audioData, int num_frames, int input_channels, int output_channels);
    void setOboeSampleRate(int32_t sample_rate);
    void setOboeBufferSize(int32_t buffer_size);
    void setOboeVolume(float volume);
    void setOboeNoiseShapingEnabled(bool enabled);
    bool isOboeInitialized();
    int32_t getOboeBufferedFrames();
}

#endif
