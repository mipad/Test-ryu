// ryujinx.cpp (完整简化版本)
#include "ryuijnx.h"
#include "FFmpegHardwareDecoder.h"
#include <chrono>
#include <csignal>
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>

// FFmpeg 头文件
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/hwcontext.h>
#include <libavutil/opt.h>
#include <libavutil/imgutils.h>
#include <libavcodec/jni.h>
}

// 全局变量定义
long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative;
std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

// 日志标签
#define LOG_TAG_NATIVE "RyujinxNative"
#define LOGI_NATIVE(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG_NATIVE, __VA_ARGS__)
#define LOGW_NATIVE(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG_NATIVE, __VA_ARGS__)
#define LOGE_NATIVE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG_NATIVE, __VA_ARGS__)
#define LOGD_NATIVE(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG_NATIVE, __VA_ARGS__)

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
    auto nativeWindow = (ANativeWindow *) native_surface;
    VkSurfaceKHR surface;
    auto vkInstance = (VkInstance) instance;
    auto fpCreateAndroidSurfaceKHR =
            reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(vkGetInstanceProcAddr(vkInstance,
                                                                                  "vkCreateAndroidSurfaceKHR"));
    if (!fpCreateAndroidSurfaceKHR)
        return -1;
    VkAndroidSurfaceCreateInfoKHR info = {VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR};
    info.window = nativeWindow;
    VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
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
    const char *cparam = env->GetStringUTFChars(jS, 0);
    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    strcpy(s, cparam);
    env->ReleaseStringUTFChars(jS, cparam);

    return s;
}

jstring createString(
        JNIEnv *env,
        char *ch) {
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

    _renderingThreadIdNative = currentId;

    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

// JNI_OnLoad - 设置 JavaVM 给 FFmpeg
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    // 设置JavaVM给FFmpeg，否则无法进行硬解码
    av_jni_set_java_vm(vm, nullptr);
    
    LOGI_NATIVE("FFmpeg JNI_OnLoad called, JavaVM set for hardware decoding");
    
    // 初始化 FFmpeg
    avformat_network_init();
    
    // 初始化硬件解码器
    FFmpegHardwareDecoder::GetInstance().Initialize();
    
    return JNI_VERSION_1_6;
}

// JNI_OnUnload
JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    // 清理硬件解码器资源
    FFmpegHardwareDecoder::GetInstance().Cleanup();
    
    avformat_network_deinit();
    
    LOGI_NATIVE("FFmpeg JNI_OnUnload called");
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    auto success = env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

bool isInitialOrientationFlipped = true;

extern "C"
void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1)
        return;
    auto nativeWindow = (ANativeWindow *) native_window;

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;

    transform = transform >> 1;

    // transform is a valid VkSurfaceTransformFlagBitsKHR
    switch (transform) {
        case 0x1:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
        case 0x2:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90;
            break;
        case 0x4:
            nativeTransform = isInitialOrientationFlipped
                              ? ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY
                              : ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_180;
            break;
        case 0x8:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_270;
            break;
        case 0x10:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL;
            break;
        case 0x20:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x40:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL;
            break;
        case 0x80:
            nativeTransform = static_cast<ANativeWindowTransform>(
                    ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL |
                    ANATIVEWINDOW_TRANSFORM_ROTATE_90);
            break;
        case 0x100:
            nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
            break;
    }

    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM,
                          static_cast<int32_t>(nativeTransform));
}

extern "C"
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    auto libPath = getStringPointer(env, native_lib_path);
    auto privateAppsPath = getStringPointer(env, private_apps_path);
    auto driverName = getStringPointer(env, driver_name);

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

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->maxSwapInterval;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz,
                                                          jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->minSwapInterval;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz,
                                                       jlong native_window, jint swap_interval) {
    auto nativeWindow = (ANativeWindow *) native_window;

    return nativeWindow->setSwapInterval(nativeWindow, swap_interval);
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

// =============== Oboe Audio JNI 接口 (修复版本) ===============
extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    bool result = RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI initOboeAudio: %dHz %dch -> %s", sample_rate, channel_count, result ? "success" : "failed");
    return result ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI shutdownOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Shutdown();
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jshortArray audio_data, jint num_frames) {
    if (!audio_data || num_frames <= 0) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "JNI writeOboeAudio: invalid parameters");
        return JNI_FALSE;
    }

    jsize length = env->GetArrayLength(audio_data);
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    
    if (data) {
        bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        
        if (!success) {
            __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "JNI writeOboeAudio: write failed, %d frames", num_frames);
        }
        return success ? JNI_TRUE : JNI_FALSE;
    }
    
    __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "JNI writeOboeAudio: failed to get array elements");
    return JNI_FALSE;
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI setOboeVolume: %.2f", volume);
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetVolume(volume);
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    bool initialized = RyujinxOboe::OboeAudioRenderer::GetInstance().IsInitialized();
    return initialized ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(JNIEnv *env, jobject thiz) {
    bool playing = RyujinxOboe::OboeAudioRenderer::GetInstance().IsPlaying();
    return playing ? JNI_TRUE : JNI_FALSE;
}

extern "C"
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    int32_t buffered = RyujinxOboe::OboeAudioRenderer::GetInstance().GetBufferedFrames();
    return static_cast<jint>(buffered);
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(JNIEnv *env, jobject thiz) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "JNI resetOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== 设备信息获取函数 ===============
extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char model[PROP_VALUE_MAX];
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX];
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

// =============== FFmpeg 硬件解码初始化 ===============
extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_initFFmpegHardwareDecoder(JNIEnv* env, jclass clazz) {
    bool success = FFmpegHardwareDecoder::GetInstance().Initialize();
    LOGI_NATIVE("FFmpeg hardware decoder initialization: %s", success ? "success" : "failed");
}

extern "C"
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_cleanupFFmpegHardwareDecoder(JNIEnv* env, jclass clazz) {
    FFmpegHardwareDecoder::GetInstance().Cleanup();
    LOGI_NATIVE("FFmpeg hardware decoder cleaned up");
}

// 注意：所有硬件解码相关的 JNI 函数已移至 FFmpegHardwareJNI.cpp 以避免重复符号错误

extern "C"
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_testHardwareDecoder(JNIEnv *env, jobject thiz) {
    return env->NewStringUTF("Hardware decoder test function - use NativeHelpers class for full functionality");
}

// =============== Oboe Audio C 接口 (for C# P/Invoke) ===============
extern "C"
bool initOboeAudio(int sample_rate, int channel_count) {
    bool result = RyujinxOboe::OboeAudioRenderer::GetInstance().Initialize(sample_rate, channel_count);
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C initOboeAudio: %dHz %dch -> %s", sample_rate, channel_count, result ? "success" : "failed");
    return result;
}

extern "C"
void shutdownOboeAudio() {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C shutdownOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Shutdown();
}

extern "C"
bool writeOboeAudio(const int16_t* data, int32_t num_frames) {
    if (!data || num_frames <= 0) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "C writeOboeAudio: invalid parameters");
        return false;
    }
    
    bool success = RyujinxOboe::OboeAudioRenderer::GetInstance().WriteAudio(data, num_frames);
    if (!success) {
        __android_log_print(ANDROID_LOG_WARN, "RyujinxOboe", "C writeOboeAudio: write failed, %d frames", num_frames);
    }
    return success;
}

extern "C"
void setOboeVolume(float volume) {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C setOboeVolume: %.2f", volume);
    RyujinxOboe::OboeAudioRenderer::GetInstance().SetVolume(volume);
}

extern "C"
bool isOboeInitialized() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsInitialized();
}

extern "C"
bool isOboePlaying() {
    return RyujinxOboe::OboeAudioRenderer::GetInstance().IsPlaying();
}

extern "C"
int32_t getOboeBufferedFrames() {
    return static_cast<int32_t>(RyujinxOboe::OboeAudioRenderer::GetInstance().GetBufferedFrames());
}

extern "C"
void resetOboeAudio() {
    __android_log_print(ANDROID_LOG_INFO, "RyujinxOboe", "C resetOboeAudio");
    RyujinxOboe::OboeAudioRenderer::GetInstance().Reset();
}

// =============== 设备信息获取 C 接口 ===============
extern "C"
const char* GetAndroidDeviceModel() {
    static char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') {
        __system_property_get("ro.product.model", model);
    }
    return model;
}

extern "C"
const char* GetAndroidDeviceBrand() {
    static char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') {
        __system_property_get("ro.product.brand", brand);
    }
    return brand;
}

// =============== FFmpeg 硬件解码 C 接口 ===============
extern "C"
bool InitializeFFmpegHardwareDecoder() {
    return FFmpegHardwareDecoder::GetInstance().Initialize();
}

extern "C"
void CleanupFFmpegHardwareDecoder() {
    FFmpegHardwareDecoder::GetInstance().Cleanup();
}

extern "C"
long CreateHardwareDecoderContext(const char* codecName) {
    JNIEnv* env = nullptr;
    JavaVM* vm = nullptr;
    
    // 获取 JNIEnv
    if (av_jni_get_java_vm(&vm) == 0 && vm) {
        jint result = vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
        if (result == JNI_EDETACHED) {
            if (vm->AttachCurrentThread(&env, nullptr) != 0) {
                return 0;
            }
        }
    }
    
    long contextId = FFmpegHardwareDecoder::GetInstance().CreateHardwareDecoderContext(env, codecName);
    
    if (env && vm) {
        vm->DetachCurrentThread();
    }
    
    return contextId;
}

extern "C"
int DecodeVideoFrame(long contextId, const uint8_t* inputData, int inputSize,
                    int* width, int* height, int* format,
                    uint8_t* plane0, int plane0Size,
                    uint8_t* plane1, int plane1Size, 
                    uint8_t* plane2, int plane2Size) {
    
    JNIEnv* env = nullptr;
    JavaVM* vm = nullptr;
    bool attached = false;
    
    // 获取 JNIEnv
    if (av_jni_get_java_vm(&vm) == 0 && vm) {
        jint result = vm->GetEnv(reinterpret_cast<void**>(&env), JNI_VERSION_1_6);
        if (result == JNI_EDETACHED) {
            if (vm->AttachCurrentThread(&env, nullptr) == 0) {
                attached = true;
            }
        }
    }
    
    if (!env) {
        return -1;
    }
    
    // 创建 Java 数组
    jbyteArray inputDataArray = env->NewByteArray(inputSize);
    jintArray frameInfoArray = env->NewIntArray(6);
    jobjectArray planeDataArray = env->NewObjectArray(3, env->FindClass("[B"), nullptr);
    
    // 设置输入数据
    env->SetByteArrayRegion(inputDataArray, 0, inputSize, reinterpret_cast<const jbyte*>(inputData));
    
    // 设置平面数据数组
    jbyteArray plane0Array = env->NewByteArray(plane0Size);
    jbyteArray plane1Array = env->NewByteArray(plane1Size);
    jbyteArray plane2Array = env->NewByteArray(plane2Size);
    
    env->SetObjectArrayElement(planeDataArray, 0, plane0Array);
    env->SetObjectArrayElement(planeDataArray, 1, plane1Array);
    env->SetObjectArrayElement(planeDataArray, 2, plane2Array);
    
    // 解码帧
    int result = FFmpegHardwareDecoder::GetInstance().DecodeVideoFrame(
        contextId, inputDataArray, inputSize, frameInfoArray, planeDataArray);
    
    if (result == 0) {
        // 获取帧信息
        jint frameInfo[6];
        env->GetIntArrayRegion(frameInfoArray, 0, 6, frameInfo);
        
        *width = frameInfo[0];
        *height = frameInfo[1];
        *format = frameInfo[2];
        
        // 获取平面数据
        if (plane0 && plane0Size > 0) {
            jbyte* plane0Data = env->GetByteArrayElements(plane0Array, nullptr);
            int actualSize = env->GetArrayLength(plane0Array);
            memcpy(plane0, plane0Data, actualSize < plane0Size ? actualSize : plane0Size);
            env->ReleaseByteArrayElements(plane0Array, plane0Data, JNI_ABORT);
        }
        
        if (plane1 && plane1Size > 0) {
            jbyte* plane1Data = env->GetByteArrayElements(plane1Array, nullptr);
            int actualSize = env->GetArrayLength(plane1Array);
            memcpy(plane1, plane1Data, actualSize < plane1Size ? actualSize : plane1Size);
            env->ReleaseByteArrayElements(plane1Array, plane1Data, JNI_ABORT);
        }
        
        if (plane2 && plane2Size > 0) {
            jbyte* plane2Data = env->GetByteArrayElements(plane2Array, nullptr);
            int actualSize = env->GetArrayLength(plane2Array);
            memcpy(plane2, plane2Data, actualSize < plane2Size ? actualSize : plane2Size);
            env->ReleaseByteArrayElements(plane2Array, plane2Data, JNI_ABORT);
        }
    }
    
    // 清理本地引用
    env->DeleteLocalRef(inputDataArray);
    env->DeleteLocalRef(frameInfoArray);
    env->DeleteLocalRef(plane0Array);
    env->DeleteLocalRef(plane1Array);
    env->DeleteLocalRef(plane2Array);
    env->DeleteLocalRef(planeDataArray);
    
    if (attached) {
        vm->DetachCurrentThread();
    }
    
    return result;
}

extern "C"
void DestroyHardwareDecoderContext(long contextId) {
    FFmpegHardwareDecoder::GetInstance().DestroyHardwareDecoderContext(contextId);
}

extern "C"
void FlushHardwareDecoder(long contextId) {
    FFmpegHardwareDecoder::GetInstance().FlushDecoder(contextId);
}

extern "C"
const char* GetFFmpegVersionString() {
    return FFmpegHardwareDecoder::GetInstance().GetFFmpegVersion();
}
