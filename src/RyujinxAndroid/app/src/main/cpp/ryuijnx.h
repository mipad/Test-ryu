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

// A macro to pass call to Vulkan and check for return value for success
#define CALL_VK(func)                                                 \
  if (VK_SUCCESS != (func)) {                                         \
    __android_log_print(ANDROID_LOG_ERROR, "Tutorial ",               \
                        "Vulkan error. File[%s], line[%d]", __FILE__, \
                        __LINE__);                                    \
    assert(false);                                                    \
  }

// A macro to check value is VK_SUCCESS
// Used also for non-vulkan functions but return VK_SUCCESS
#define VK_CHECK(x)  CALL_VK(x)

#define LoadLib(a) dlopen(a, RTLD_NOW)

void *_ryujinxNative = NULL;

// Ryujinx imported functions
bool (*initialize)(char *) = NULL;

long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;

// ===== 添加画面比例设置支持 =====
#include <android/log.h>  // 确保已包含日志头文件

// 画面比例枚举定义
enum class AspectRatio {
    Fixed16x9 = 0,  // 16:9
    Fixed4x3 = 1,    // 4:3
    Stretched = 2    // 拉伸模式
};

// 图形配置结构
struct GraphicsConfig {
    AspectRatio AspectRatio;
    // 可以添加其他图形设置项
};

// 全局配置结构
struct Config {
    GraphicsConfig Graphics;
};

// 声明全局配置对象
extern Config config;

// 声明 JNI 函数
#ifdef __cplusplus
extern "C" {
#endif

JNIEXPORT void JNICALL
Java_org_ryujinx_RyujinxNative_graphicsSetAspectRatio(
    JNIEnv *env,
    jobject instance,
    jint aspectRatio);

#ifdef __cplusplus
}
#endif

#endif //RYUJINXNATIVE_RYUIJNX_H
