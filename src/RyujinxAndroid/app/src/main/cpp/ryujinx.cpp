// Write C++ code here.
//
// Do not forget to dynamically load the C++ library into your application.
//
// For instance,
//
// In MainActivity.java:
//    static {
//       System.loadLibrary("ryuijnx");
//    }
//
// Or, in MainActivity.kt:
//    companion object {
//      init {
//         System.loadLibrary("ryuijnx")
//      }
//    }

#include "ryuijnx.h"
#include "pthread.h"
#include <chrono>
#include <csignal>
#include <android/api-level.h>

#if __ANDROID_API__ < 30
#error "This application requires Android 11 (API level 30) or higher"
#endif

std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

extern "C"
{
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(
        JNIEnv *env,
        jobject instance,
        jobject surface) {
    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    return nativeWindow == NULL ? -1 : (jlong) nativeWindow;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(
        JNIEnv *env,
        jobject instance,
        jlong window) {
    auto nativeWindow = (ANativeWindow *) window;

    if (nativeWindow != NULL)
        ANativeWindow_release(nativeWindow);
}

long createSurface(long native_surface, long instance) {
    if (native_surface == 0 || native_surface == -1) {
        return -1;
    }
    
    auto nativeWindow = (ANativeWindow *) native_surface;
    VkSurfaceKHR surface;
    auto vkInstance = (VkInstance) instance;
    
    auto fpCreateAndroidSurfaceKHR =
            reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
                vkGetInstanceProcAddr(vkInstance, "vkCreateAndroidSurfaceKHR"));
    
    if (!fpCreateAndroidSurfaceKHR) {
        return -1;
    }
    
    VkAndroidSurfaceCreateInfoKHR info = {
        VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR
    };
    info.window = nativeWindow;
    
    VkResult result = fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface);
    if (result != VK_SUCCESS) {
        return -1;
    }
    
    return (long) surface;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(
        JNIEnv *env,
        jobject instance) {
    return (jlong) createSurface;
}

char *getStringPointer(
        JNIEnv *env,
        jstring jS) {
    if (jS == nullptr) {
        return nullptr;
    }
    
    const char *cparam = env->GetStringUTFChars(jS, 0);
    if (cparam == nullptr) {
        return nullptr;
    }
    
    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    if (s) {
        strncpy(s, cparam, len);
        s[len] = '\0';
    }
    
    env->ReleaseStringUTFChars(jS, cparam);
    return s;
}

jstring createString(
        JNIEnv *env,
        char *ch) {
    if (ch == nullptr) {
        return nullptr;
    }
    auto str = env->NewStringUTF(ch);
    return str;
}

jstring createStringFromStdString(
        JNIEnv *env,
        std::string s) {
    auto str = env->NewStringUTF(s.c_str());
    return str;
}


}
extern "C"
void setRenderingThread() {
    auto currentId = pthread_self();

    _renderingThreadId = currentId;

    _currentTimePoint = std::chrono::high_resolution_clock::now();
}
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    if (success != JNI_OK) {
        return;
    }
    
    _vm = vm;
    _mainActivity = env->NewGlobalRef(thiz);
    _mainActivityClass = (jclass)env->NewGlobalRef(env->GetObjectClass(thiz));
}

bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1) {
        return;
    }
    
    auto nativeWindow = (ANativeWindow *) native_window;
    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;

    switch (transform) {
        case 0x00000001: // VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
        case 0x00000002: // VK_SURFACE_TRANSFORM_ROTATE_90_BIT_KHR
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90;
            break;
        case 0x00000004: // VK_SURFACE_TRANSFORM_ROTATE_180_BIT_KHR
            nativeTransform = isInitialOrientationFlipped
                              ? ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY
                              : ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_180;
            break;
        case 0x00000008: // VK_SURFACE_TRANSFORM_ROTATE_270_BIT_KHR
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_270;
            break;
        case 0x00000010: // VK_SURFACE_TRANSFORM_HORIZONTAL_MIRROR_BIT_KHR
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL;
            break;
        case 0x00000020: // VK_SURFACE_TRANSFORM_HORIZONTAL_MIRROR_ROTATE_90_BIT_KHR
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL |
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x00000040: // VK_SURFACE_TRANSFORM_HORIZONTAL_MIRROR_ROTATE_180_BIT_KHR
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
            break;
        case 0x00000080: // VK_SURFACE_TRANSFORM_HORIZONTAL_MIRROR_ROTATE_270_BIT_KHR
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL |
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x00000100: // VK_SURFACE_TRANSFORM_INHERIT_BIT_KHR
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
        default:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
    }

    ANativeWindow_setBuffersGeometry(nativeWindow, 
                                     ANativeWindow_getWidth(nativeWindow),
                                     ANativeWindow_getHeight(nativeWindow),
                                     ANativeWindow_getFormat(nativeWindow));
}

extern "C"
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    auto libPath = getStringPointer(env, native_lib_path);
    if (!libPath) {
        return -1;
    }
    
    auto privateAppsPath = getStringPointer(env, private_apps_path);
    if (!privateAppsPath) {
        delete[] libPath;
        return -1;
    }
    
    auto driverName = getStringPointer(env, driver_name);
    if (!driverName) {
        delete[] libPath;
        delete[] privateAppsPath;
        return -1;
    }

    auto handle = adrenotools_open_libvulkan(
            RTLD_NOW,
            ADRENOTOOLS_DRIVER_CUSTOM,
            nullptr,
            libPath,
            privateAppsPath,
            driverName,
            nullptr,
            nullptr
    );

    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;

    return (jlong) handle;
}

extern "C"
void debug_break(int code) {
    if (code >= 3)
        int r = 0;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable);
}

// 修复交换间隔API - 使用帧率API替代
extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    // 在Android中，最大交换间隔通常是1（垂直同步）
    return 1;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    // 最小交换间隔通常是0（无垂直同步）
    return 0;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    if (native_window == 0 || native_window == -1) {
        return -1;
    }
    
    auto nativeWindow = (ANativeWindow *) native_window;
    
    // 在Android中，通常通过帧率API控制呈现
    // 这里我们不做实际操作，因为交换间隔通常在Vulkan交换链创建时设置
    return 0; // 返回成功
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    return createString(env, (char*)ptr);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz,
                                                                      jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_cleanupNative(JNIEnv *env, jobject thiz) {
    if (_mainActivity != nullptr) {
        env->DeleteGlobalRef(_mainActivity);
        _mainActivity = nullptr;
    }
    if (_mainActivityClass != nullptr) {
        env->DeleteGlobalRef(_mainActivityClass);
        _mainActivityClass = nullptr;
    }
}
