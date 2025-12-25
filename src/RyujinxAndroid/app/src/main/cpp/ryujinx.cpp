#include "ryuijnx.h"
#include "oboe_audio_renderer.h"
#include <chrono>
#include <csignal>
#include <android/log.h>
#include <stdarg.h>
#include <sys/system_properties.h>
#include <map>
#include <memory>
#include <vector>
#include <queue>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>

// MediaCodec 解码器实现
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>

#define LOG_TAG "RyujinxNative"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

// 全局变量定义
long _renderingThreadId = 0;
JavaVM *_vm = nullptr;
jobject _mainActivity = nullptr;
jclass _mainActivityClass = nullptr;
pthread_t _renderingThreadIdNative;
std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;
bool isInitialOrientationFlipped = true;

// Oboe音频渲染器单例
RyujinxOboe::OboeAudioRenderer* g_singleton_renderer = nullptr;

// ========== MediaCodec 解码器实现 ==========

// 解码器管理器
class MediaCodecDecoderManager {
private:
    std::map<MediaCodecDecoderHandle, AMediaCodec*> decoders_;
    std::map<MediaCodecDecoderHandle, AMediaFormat*> formats_;
    std::map<MediaCodecDecoderHandle, std::thread*> outputThreads_;
    std::map<MediaCodecDecoderHandle, std::atomic<bool>> runningFlags_;
    std::map<MediaCodecDecoderHandle, std::queue<std::vector<uint8_t>>> frameQueues_;
    std::map<MediaCodecDecoderHandle, std::mutex*> queueMutexes_;
    std::map<MediaCodecDecoderHandle, std::condition_variable*> queueCVs_;
    std::atomic<long> nextHandleId_{1};
    std::mutex managerMutex_;
    
public:
    static MediaCodecDecoderManager& GetInstance() {
        static MediaCodecDecoderManager instance;
        return instance;
    }
    
    MediaCodecDecoderHandle CreateDecoder(MediaCodecType codec_type) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(nextHandleId_++);
        
        // 根据编解码器类型创建 MediaCodec
        const char* mime_type = nullptr;
        switch (codec_type) {
            case MEDIACODEC_H264:
                mime_type = "video/avc";
                break;
            case MEDIACODEC_VP8:
                mime_type = "video/x-vnd.on2.vp8";
                break;
            case MEDIACODEC_VP9:
                mime_type = "video/x-vnd.on2.vp9";
                break;
            case MEDIACODEC_HEVC:
                mime_type = "video/hevc";
                break;
            case MEDIACODEC_AV1:
                mime_type = "video/av01";
                break;
            default:
                LOGE("Unsupported codec type: %d", codec_type);
                return nullptr;
        }
        
        AMediaCodec* codec = AMediaCodec_createDecoderByType(mime_type);
        if (!codec) {
            LOGE("Failed to create MediaCodec decoder for %s", mime_type);
            return nullptr;
        }
        
        decoders_[handle] = codec;
        formats_[handle] = nullptr;
        runningFlags_[handle] = false;
        queueMutexes_[handle] = new std::mutex();
        queueCVs_[handle] = new std::condition_variable();
        
        LOGI("Created MediaCodec decoder handle: %p for %s", handle, mime_type);
        return handle;
    }
    
    bool InitDecoder(MediaCodecDecoderHandle handle,
                    int width, int height,
                    int frame_rate,
                    int color_format,
                    const uint8_t* csd0, int csd0_size,
                    const uint8_t* csd1, int csd1_size,
                    const uint8_t* csd2, int csd2_size) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        auto it = decoders_.find(handle);
        if (it == decoders_.end()) {
            LOGE("Decoder not found: %p", handle);
            return false;
        }
        
        AMediaCodec* codec = it->second;
        
        // 创建 MediaFormat
        AMediaFormat* format = AMediaFormat_new();
        if (!format) {
            LOGE("Failed to create MediaFormat");
            return false;
        }
        
        // 设置基础参数
        const char* mime_type = "video/avc"; // 默认 H.264
        auto decoderIt = decoders_.find(handle);
        // 这里可以根据需要确定实际的 MIME 类型
        AMediaFormat_setString(format, AMEDIAFORMAT_KEY_MIME, mime_type);
        AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_WIDTH, width);
        AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_HEIGHT, height);
        AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_FRAME_RATE, frame_rate);
        AMediaFormat_setInt32(format, AMEDIAFORMAT_KEY_COLOR_FORMAT, color_format);
        
        // 设置 CSD 数据
        if (csd0 && csd0_size > 0) {
            AMediaFormat_setBuffer(format, "csd-0", csd0, csd0_size);
        }
        
        if (csd1 && csd1_size > 0) {
            AMediaFormat_setBuffer(format, "csd-1", csd1, csd1_size);
        }
        
        if (csd2 && csd2_size > 0) {
            AMediaFormat_setBuffer(format, "csd-2", csd2, csd2_size);
        }
        
        // 配置解码器
        media_status_t status = AMediaCodec_configure(codec, format, nullptr, nullptr, 0);
        if (status != AMEDIA_OK) {
            LOGE("AMediaCodec_configure failed: %d", status);
            AMediaFormat_delete(format);
            return false;
        }
        
        // 保存格式
        formats_[handle] = format;
        
        LOGI("Initialized MediaCodec decoder %p: %dx%d", handle, width, height);
        return true;
    }
    
    bool StartDecoder(MediaCodecDecoderHandle handle) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        auto it = decoders_.find(handle);
        if (it == decoders_.end()) {
            LOGE("Decoder not found: %p", handle);
            return false;
        }
        
        AMediaCodec* codec = it->second;
        
        // 启动解码器
        media_status_t status = AMediaCodec_start(codec);
        if (status != AMEDIA_OK) {
            LOGE("AMediaCodec_start failed: %d", status);
            return false;
        }
        
        // 设置运行标志
        runningFlags_[handle] = true;
        
        // 启动输出线程
        std::thread* outputThread = new std::thread([this, handle]() {
            OutputThreadFunc(handle);
        });
        outputThreads_[handle] = outputThread;
        
        LOGI("Started MediaCodec decoder: %p", handle);
        return true;
    }
    
    bool DecodeFrame(MediaCodecDecoderHandle handle,
                    const uint8_t* frame_data, int frame_size,
                    long long presentation_time_us,
                    int flags) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        auto it = decoders_.find(handle);
        if (it == decoders_.end()) {
            LOGE("Decoder not found: %p", handle);
            return false;
        }
        
        if (!runningFlags_[handle]) {
            LOGE("Decoder not running: %p", handle);
            return false;
        }
        
        AMediaCodec* codec = it->second;
        
        // 获取输入缓冲区
        ssize_t inputBufferIndex = AMediaCodec_dequeueInputBuffer(codec, 10000);
        if (inputBufferIndex < 0) {
            if (inputBufferIndex == AMEDIACODEC_INFO_TRY_AGAIN_LATER) {
                LOGD("No input buffer available");
            }
            return false;
        }
        
        // 获取输入缓冲区
        size_t bufferSize = 0;
        uint8_t* inputBuffer = AMediaCodec_getInputBuffer(codec, inputBufferIndex, &bufferSize);
        if (!inputBuffer) {
            LOGE("Failed to get input buffer");
            return false;
        }
        
        if (frame_size > static_cast<int>(bufferSize)) {
            LOGE("Frame too large: %d > %zu", frame_size, bufferSize);
            return false;
        }
        
        // 复制数据
        memcpy(inputBuffer, frame_data, frame_size);
        
        // 提交输入缓冲区
        media_status_t status = AMediaCodec_queueInputBuffer(codec,
                                                            inputBufferIndex,
                                                            0,
                                                            frame_size,
                                                            presentation_time_us,
                                                            flags);
        if (status != AMEDIA_OK) {
            LOGE("AMediaCodec_queueInputBuffer failed: %d", status);
            return false;
        }
        
        return true;
    }
    
    bool GetDecodedFrameYUV(MediaCodecDecoderHandle handle,
                           uint8_t** yuv_data, int* yuv_size,
                           int* width, int* height,
                           int timeout_us) {
        auto mutexIt = queueMutexes_.find(handle);
        auto cvIt = queueCVs_.find(handle);
        auto queueIt = frameQueues_.find(handle);
        
        if (mutexIt == queueMutexes_.end() || cvIt == queueCVs_.end() || queueIt == frameQueues_.end()) {
            return false;
        }
        
        std::unique_lock<std::mutex> lock(*(mutexIt->second));
        
        // 等待帧可用
        if (queueIt->second.empty()) {
            if (timeout_us > 0) {
                auto timeout = std::chrono::microseconds(timeout_us);
                if (!cvIt->second->wait_for(lock, timeout, [&queueIt]() { return !queueIt->second.empty(); })) {
                    return false; // 超时
                }
            } else {
                cvIt->second->wait(lock, [&queueIt]() { return !queueIt->second.empty(); });
            }
        }
        
        if (queueIt->second.empty()) {
            return false;
        }
        
        // 获取帧数据
        std::vector<uint8_t> frame = std::move(queueIt->second.front());
        queueIt->second.pop();
        
        // 分配输出内存
        *yuv_size = static_cast<int>(frame.size());
        *yuv_data = static_cast<uint8_t*>(malloc(*yuv_size));
        if (!*yuv_data) {
            return false;
        }
        
        memcpy(*yuv_data, frame.data(), *yuv_size);
        
        // 这里简化处理，实际需要从帧中提取宽度和高度
        // 这里假设宽度和高度已经知道，或者可以从其他方式获取
        *width = 0;
        *height = 0;
        
        return true;
    }
    
    bool StopDecoder(MediaCodecDecoderHandle handle) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        auto it = decoders_.find(handle);
        if (it == decoders_.end()) {
            LOGE("Decoder not found: %p", handle);
            return false;
        }
        
        // 停止运行标志
        runningFlags_[handle] = false;
        
        // 停止输出线程
        auto threadIt = outputThreads_.find(handle);
        if (threadIt != outputThreads_.end() && threadIt->second) {
            threadIt->second->join();
            delete threadIt->second;
            outputThreads_.erase(handle);
        }
        
        // 停止解码器
        AMediaCodec_stop(it->second);
        
        // 清空帧队列
        auto queueIt = frameQueues_.find(handle);
        if (queueIt != frameQueues_.end()) {
            std::queue<std::vector<uint8_t>> emptyQueue;
            queueIt->second.swap(emptyQueue);
        }
        
        LOGI("Stopped MediaCodec decoder: %p", handle);
        return true;
    }
    
    void DestroyDecoder(MediaCodecDecoderHandle handle) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        // 停止解码器
        StopDecoder(handle);
        
        // 清理资源
        auto decoderIt = decoders_.find(handle);
        if (decoderIt != decoders_.end()) {
            AMediaCodec_delete(decoderIt->second);
            decoders_.erase(handle);
        }
        
        auto formatIt = formats_.find(handle);
        if (formatIt != formats_.end()) {
            AMediaFormat_delete(formatIt->second);
            formats_.erase(handle);
        }
        
        auto mutexIt = queueMutexes_.find(handle);
        if (mutexIt != queueMutexes_.end()) {
            delete mutexIt->second;
            queueMutexes_.erase(handle);
        }
        
        auto cvIt = queueCVs_.find(handle);
        if (cvIt != queueCVs_.end()) {
            delete cvIt->second;
            queueCVs_.erase(handle);
        }
        
        frameQueues_.erase(handle);
        runningFlags_.erase(handle);
        
        LOGI("Destroyed MediaCodec decoder: %p", handle);
    }
    
    bool IsCodecSupported(MediaCodecType codec_type) {
        const char* mime_type = nullptr;
        switch (codec_type) {
            case MEDIACODEC_H264:
                mime_type = "video/avc";
                break;
            case MEDIACODEC_VP8:
                mime_type = "video/x-vnd.on2.vp8";
                break;
            case MEDIACODEC_VP9:
                mime_type = "video/x-vnd.on2.vp9";
                break;
            case MEDIACODEC_HEVC:
                mime_type = "video/hevc";
                break;
            default:
                return false;
        }
        
        AMediaCodec* codec = AMediaCodec_createDecoderByType(mime_type);
        if (codec) {
            AMediaCodec_delete(codec);
            LOGI("Codec %s is supported", mime_type);
            return true;
        }
        
        LOGW("Codec %s is not supported", mime_type);
        return false;
    }
    
    DecoderStatus GetDecoderStatus(MediaCodecDecoderHandle handle) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        auto it = decoders_.find(handle);
        if (it == decoders_.end()) {
            return DECODER_STATUS_ERROR;
        }
        
        auto runningIt = runningFlags_.find(handle);
        if (runningIt == runningFlags_.end()) {
            return DECODER_STATUS_UNINITIALIZED;
        }
        
        if (runningIt->second) {
            return DECODER_STATUS_RUNNING;
        }
        
        auto formatIt = formats_.find(handle);
        if (formatIt != formats_.end() && formatIt->second) {
            return DECODER_STATUS_INITIALIZED;
        }
        
        return DECODER_STATUS_STOPPED;
    }
    
    bool FlushDecoder(MediaCodecDecoderHandle handle) {
        std::lock_guard<std::mutex> lock(managerMutex_);
        
        auto it = decoders_.find(handle);
        if (it == decoders_.end()) {
            return false;
        }
        
        media_status_t status = AMediaCodec_flush(it->second);
        if (status != AMEDIA_OK) {
            LOGE("AMediaCodec_flush failed: %d", status);
            return false;
        }
        
        // 清空帧队列
        auto queueIt = frameQueues_.find(handle);
        if (queueIt != frameQueues_.end()) {
            std::queue<std::vector<uint8_t>> emptyQueue;
            queueIt->second.swap(emptyQueue);
        }
        
        LOGI("Flushed MediaCodec decoder: %p", handle);
        return true;
    }
    
private:
    void OutputThreadFunc(MediaCodecDecoderHandle handle) {
        LOGD("Output thread started for decoder: %p", handle);
        
        auto decoderIt = decoders_.find(handle);
        auto runningIt = runningFlags_.find(handle);
        auto mutexIt = queueMutexes_.find(handle);
        auto cvIt = queueCVs_.find(handle);
        auto queueIt = frameQueues_.find(handle);
        
        if (decoderIt == decoders_.end() || runningIt == runningFlags_.end() ||
            mutexIt == queueMutexes_.end() || cvIt == queueCVs_.end() || queueIt == frameQueues_.end()) {
            return;
        }
        
        AMediaCodec* codec = decoderIt->second;
        
        while (runningIt->second) {
            AMediaCodecBufferInfo bufferInfo;
            ssize_t outputBufferIndex = AMediaCodec_dequeueOutputBuffer(codec, &bufferInfo, 10000);
            
            if (outputBufferIndex >= 0) {
                // 获取输出缓冲区
                size_t bufferSize = 0;
                uint8_t* outputBuffer = AMediaCodec_getOutputBuffer(codec, outputBufferIndex, &bufferSize);
                
                if (outputBuffer && bufferInfo.size > 0) {
                    // 提取 YUV 数据
                    std::vector<uint8_t> yuvData(bufferInfo.size);
                    memcpy(yuvData.data(), outputBuffer + bufferInfo.offset, bufferInfo.size);
                    
                    // 添加到队列
                    {
                        std::lock_guard<std::mutex> lock(*(mutexIt->second));
                        queueIt->second.push(std::move(yuvData));
                    }
                    cvIt->second->notify_one();
                }
                
                // 释放输出缓冲区
                AMediaCodec_releaseOutputBuffer(codec, outputBufferIndex, false);
                
                // 检查结束标志
                if ((bufferInfo.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0) {
                    LOGD("End of stream for decoder: %p", handle);
                    break;
                }
            } else if (outputBufferIndex == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED) {
                // 输出格式改变
                AMediaFormat* format = AMediaCodec_getOutputFormat(codec);
                if (format) {
                    int width, height, colorFormat;
                    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_WIDTH, &width);
                    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_HEIGHT, &height);
                    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_COLOR_FORMAT, &colorFormat);
                    
                    LOGI("Output format changed: %dx%d, color: %d", width, height, colorFormat);
                    AMediaFormat_delete(format);
                }
            } else if (outputBufferIndex == AMEDIACODEC_INFO_TRY_AGAIN_LATER) {
                // 没有可用输出缓冲区，短暂休眠
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            } else {
                // 其他错误
                LOGE("Error dequeueing output buffer: %zd", outputBufferIndex);
                break;
            }
        }
        
        LOGD("Output thread stopped for decoder: %p", handle);
    }
};

// ========== C 接口函数实现 ==========

MediaCodecDecoderHandle CreateMediaCodecDecoder(MediaCodecType codec_type) {
    return MediaCodecDecoderManager::GetInstance().CreateDecoder(codec_type);
}

bool InitMediaCodecDecoder(MediaCodecDecoderHandle decoder,
                          int width, int height,
                          int frame_rate,
                          int color_format,
                          const uint8_t* csd0, int csd0_size,
                          const uint8_t* csd1, int csd1_size,
                          const uint8_t* csd2, int csd2_size) {
    return MediaCodecDecoderManager::GetInstance().InitDecoder(decoder,
                                                              width, height,
                                                              frame_rate,
                                                              color_format,
                                                              csd0, csd0_size,
                                                              csd1, csd1_size,
                                                              csd2, csd2_size);
}

bool StartMediaCodecDecoder(MediaCodecDecoderHandle decoder) {
    return MediaCodecDecoderManager::GetInstance().StartDecoder(decoder);
}

bool DecodeMediaCodecFrame(MediaCodecDecoderHandle decoder,
                          const uint8_t* frame_data, int frame_size,
                          long long presentation_time_us,
                          int flags) {
    return MediaCodecDecoderManager::GetInstance().DecodeFrame(decoder,
                                                              frame_data, frame_size,
                                                              presentation_time_us,
                                                              flags);
}

bool GetDecodedFrameYUV(MediaCodecDecoderHandle decoder,
                       uint8_t** yuv_data, int* yuv_size,
                       int* width, int* height,
                       int timeout_us) {
    return MediaCodecDecoderManager::GetInstance().GetDecodedFrameYUV(decoder,
                                                                     yuv_data, yuv_size,
                                                                     width, height,
                                                                     timeout_us);
}

bool StopMediaCodecDecoder(MediaCodecDecoderHandle decoder) {
    return MediaCodecDecoderManager::GetInstance().StopDecoder(decoder);
}

void DestroyMediaCodecDecoder(MediaCodecDecoderHandle decoder) {
    MediaCodecDecoderManager::GetInstance().DestroyDecoder(decoder);
}

bool IsMediaCodecSupported(MediaCodecType codec_type) {
    return MediaCodecDecoderManager::GetInstance().IsCodecSupported(codec_type);
}

const char* GetMediaCodecDeviceInfo() {
    static char deviceInfo[256] = {0};
    if (deviceInfo[0] == '\0') {
        char manufacturer[PROP_VALUE_MAX] = {0};
        char model[PROP_VALUE_MAX] = {0};
        char platform[PROP_VALUE_MAX] = {0};
        
        __system_property_get("ro.product.manufacturer", manufacturer);
        __system_property_get("ro.product.model", model);
        __system_property_get("ro.board.platform", platform);
        
        snprintf(deviceInfo, sizeof(deviceInfo), "%s %s (%s)", manufacturer, model, platform);
    }
    return deviceInfo;
}

DecoderStatus GetMediaCodecDecoderStatus(MediaCodecDecoderHandle decoder) {
    return MediaCodecDecoderManager::GetInstance().GetDecoderStatus(decoder);
}

bool FlushMediaCodecDecoder(MediaCodecDecoderHandle decoder) {
    return MediaCodecDecoderManager::GetInstance().FlushDecoder(decoder);
}

// ========== JNI 函数实现 ==========

extern "C" {

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getNativeWindow(JNIEnv *env, jobject instance, jobject surface) {
    auto nativeWindow = ANativeWindow_fromSurface(env, surface);
    return nativeWindow == NULL ? -1 : (jlong) nativeWindow;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_releaseNativeWindow(JNIEnv *env, jobject instance, jlong window) {
    auto nativeWindow = (ANativeWindow *) window;
    if (nativeWindow != NULL) ANativeWindow_release(nativeWindow);
}

long createSurface(long native_surface, long instance) {
    auto nativeWindow = (ANativeWindow *) native_surface;
    VkSurfaceKHR surface;
    auto vkInstance = (VkInstance) instance;
    auto fpCreateAndroidSurfaceKHR = reinterpret_cast<PFN_vkCreateAndroidSurfaceKHR>(
        vkGetInstanceProcAddr(vkInstance, "vkCreateAndroidSurfaceKHR"));
    if (!fpCreateAndroidSurfaceKHR) return -1;
    VkAndroidSurfaceCreateInfoKHR info = {VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR};
    info.window = nativeWindow;
    VK_CHECK(fpCreateAndroidSurfaceKHR(vkInstance, &info, nullptr, &surface));
    return (long) surface;
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_getCreateSurfacePtr(JNIEnv *env, jobject instance) {
    return (jlong) createSurface;
}

char *getStringPointer(JNIEnv *env, jstring jS) {
    const char *cparam = env->GetStringUTFChars(jS, 0);
    auto len = env->GetStringUTFLength(jS);
    char *s = new char[len + 1];
    strcpy(s, cparam);
    env->ReleaseStringUTFChars(jS, cparam);
    return s;
}

jstring createString(JNIEnv *env, char *ch) {
    return env->NewStringUTF(ch);
}

jstring createStringFromStdString(JNIEnv *env, std::string s) {
    return env->NewStringUTF(s.c_str());
}

void setRenderingThread() {
    _renderingThreadIdNative = pthread_self();
    _currentTimePoint = std::chrono::high_resolution_clock::now();
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) { 
    return JNI_VERSION_1_6; 
}

JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_MainActivity_initVm(JNIEnv *env, jobject thiz) {
    JavaVM *vm = nullptr;
    env->GetJavaVM(&vm);
    _vm = vm;
    _mainActivity = thiz;
    _mainActivityClass = env->GetObjectClass(thiz);
}

void setCurrentTransform(long native_window, int transform) {
    if (native_window == 0 || native_window == -1) return;
    auto nativeWindow = (ANativeWindow *) native_window;

    auto nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY;
    transform = transform >> 1;

    switch (transform) {
        case 0x1: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
        case 0x2: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_90; break;
        case 0x4: nativeTransform = isInitialOrientationFlipped ? ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY : ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_180; break;
        case 0x8: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_ROTATE_270; break;
        case 0x10: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL; break;
        case 0x20: nativeTransform = static_cast<ANativeWindowTransform>(ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_HORIZONTAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
        case 0x40: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL; break;
        case 0x80: nativeTransform = static_cast<ANativeWindowTransform>(ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_MIRROR_VERTICAL | ANATIVEWINDOW_TRANSFORM_ROTATE_90); break;
        case 0x100: nativeTransform = ANativeWindowTransform::ANATIVEWINDOW_TRANSFORM_IDENTITY; break;
    }

    nativeWindow->perform(nativeWindow, NATIVE_WINDOW_SET_BUFFERS_TRANSFORM, static_cast<int32_t>(nativeTransform));
}

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_loadDriver(JNIEnv *env, jobject thiz,
                                                  jstring native_lib_path,
                                                  jstring private_apps_path,
                                                  jstring driver_name) {
    auto libPath = getStringPointer(env, native_lib_path);
    auto privateAppsPath = getStringPointer(env, private_apps_path);
    auto driverName = getStringPointer(env, driver_name);

    auto handle = adrenotools_open_libvulkan(RTLD_NOW, ADRENOTOOLS_DRIVER_CUSTOM, nullptr,
                                            libPath, privateAppsPath, driverName, nullptr, nullptr);

    delete[] libPath;
    delete[] privateAppsPath;
    delete[] driverName;
    return (jlong) handle;
}

void debug_break(int code) {
    if (code >= 3) { 
        // 调试断点
    }
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setTurboMode(JNIEnv *env, jobject thiz, jboolean enable) {
    adrenotools_set_turbo(enable);
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMaxSwapInterval(JNIEnv *env, jobject thiz, jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;
    return nativeWindow->maxSwapInterval;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getMinSwapInterval(JNIEnv *env, jobject thiz, jlong native_window) {
    auto nativeWindow = (ANativeWindow *) native_window;
    return nativeWindow->minSwapInterval;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_setSwapInterval(JNIEnv *env, jobject thiz, jlong native_window, jint swap_interval) {
    auto nativeWindow = (ANativeWindow *) native_window;
    return nativeWindow->setSwapInterval(nativeWindow, swap_interval);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getStringJava(JNIEnv *env, jobject thiz, jlong ptr) {
    return createString(env, (char*)ptr);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setIsInitialOrientationFlipped(JNIEnv *env, jobject thiz, jboolean is_flipped) {
    isInitialOrientationFlipped = is_flipped;
}

// ========== 单例 Oboe Audio JNI接口 ==========

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudio(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeAudioWithFormat(JNIEnv *env, jobject thiz, jint sample_rate, jint channel_count, jint sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeAudio(JNIEnv *env, jobject thiz) {
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        delete g_singleton_renderer;
        g_singleton_renderer = nullptr;
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudio(JNIEnv *env, jobject thiz, jshortArray audio_data, jint num_frames) {
    if (!g_singleton_renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (data) {
        bool success = g_singleton_renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeAudioRaw(JNIEnv *env, jobject thiz, jbyteArray audio_data, jint num_frames, jint sample_format) {
    if (!g_singleton_renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (data) {
        bool success = g_singleton_renderer->WriteAudioRaw(reinterpret_cast<uint8_t*>(data), num_frames, sample_format);
        env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeVolume(JNIEnv *env, jobject thiz, jfloat volume) {
    if (g_singleton_renderer) {
        g_singleton_renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeInitialized(JNIEnv *env, jobject thiz) {
    return g_singleton_renderer && g_singleton_renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboePlaying(JNIEnv *env, jobject thiz) {
    return g_singleton_renderer && g_singleton_renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeBufferedFrames(JNIEnv *env, jobject thiz) {
    return g_singleton_renderer ? static_cast<jint>(g_singleton_renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeAudio(JNIEnv *env, jobject thiz) {
    if (g_singleton_renderer) {
        g_singleton_renderer->Reset();
    }
}

// ========== 多实例 Oboe Audio JNI接口 ==========

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createOboeRenderer(JNIEnv *env, jobject thiz) {
    auto renderer = new RyujinxOboe::OboeAudioRenderer();
    return reinterpret_cast<jlong>(renderer);
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
        delete renderer;
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr, jint sample_rate, jint channel_count, jint sample_format) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->InitializeWithFormat(sample_rate, channel_count, sample_format) ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_shutdownOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Shutdown();
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudio(JNIEnv *env, jobject thiz, jlong renderer_ptr, jshortArray audio_data, jint num_frames) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    jshort* data = env->GetShortArrayElements(audio_data, nullptr);
    if (data) {
        bool success = renderer->WriteAudio(reinterpret_cast<int16_t*>(data), num_frames);
        env->ReleaseShortArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_writeOboeRendererAudioRaw(JNIEnv *env, jobject thiz, jlong renderer_ptr, jbyteArray audio_data, jint num_frames, jint sample_format) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (!renderer || !audio_data || num_frames <= 0) return JNI_FALSE;
    jbyte* data = env->GetByteArrayElements(audio_data, nullptr);
    if (data) {
        bool success = renderer->WriteAudioRaw(reinterpret_cast<uint8_t*>(data), num_frames, sample_format);
        env->ReleaseByteArrayElements(audio_data, data, JNI_ABORT);
        return success ? JNI_TRUE : JNI_FALSE;
    }
    return JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_setOboeRendererVolume(JNIEnv *env, jobject thiz, jlong renderer_ptr, jfloat volume) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->SetVolume(volume);
    }
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererInitialized(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsInitialized() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isOboeRendererPlaying(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer && renderer->IsPlaying() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getOboeRendererBufferedFrames(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    return renderer ? static_cast<jint>(renderer->GetBufferedFrames()) : 0;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_resetOboeRenderer(JNIEnv *env, jobject thiz, jlong renderer_ptr) {
    auto renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer_ptr);
    if (renderer) {
        renderer->Reset();
    }
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceModel(JNIEnv *env, jobject thiz) {
    char model[PROP_VALUE_MAX];
    __system_property_get("ro.product.model", model);
    return env->NewStringUTF(model);
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getAndroidDeviceBrand(JNIEnv *env, jobject thiz) {
    char brand[PROP_VALUE_MAX];
    __system_property_get("ro.product.brand", brand);
    return env->NewStringUTF(brand);
}

// ========== MediaCodec JNI接口 ==========

JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createMediaCodecDecoder(
    JNIEnv* env, jobject thiz, jint codec_type) {
    MediaCodecType type = static_cast<MediaCodecType>(codec_type);
    MediaCodecDecoderHandle handle = CreateMediaCodecDecoder(type);
    return reinterpret_cast<jlong>(handle);
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id,
    jint width, jint height,
    jint frame_rate,
    jint color_format,
    jbyteArray csd0,
    jbyteArray csd1,
    jbyteArray csd2) {
    
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    
    uint8_t* csd0_data = nullptr;
    int csd0_size = 0;
    uint8_t* csd1_data = nullptr;
    int csd1_size = 0;
    uint8_t* csd2_data = nullptr;
    int csd2_size = 0;
    
    if (csd0) {
        csd0_size = env->GetArrayLength(csd0);
        csd0_data = new uint8_t[csd0_size];
        jbyte* csd0_bytes = env->GetByteArrayElements(csd0, nullptr);
        memcpy(csd0_data, csd0_bytes, csd0_size);
        env->ReleaseByteArrayElements(csd0, csd0_bytes, JNI_ABORT);
    }
    
    if (csd1) {
        csd1_size = env->GetArrayLength(csd1);
        csd1_data = new uint8_t[csd1_size];
        jbyte* csd1_bytes = env->GetByteArrayElements(csd1, nullptr);
        memcpy(csd1_data, csd1_bytes, csd1_size);
        env->ReleaseByteArrayElements(csd1, csd1_bytes, JNI_ABORT);
    }
    
    if (csd2) {
        csd2_size = env->GetArrayLength(csd2);
        csd2_data = new uint8_t[csd2_size];
        jbyte* csd2_bytes = env->GetByteArrayElements(csd2, nullptr);
        memcpy(csd2_data, csd2_bytes, csd2_size);
        env->ReleaseByteArrayElements(csd2, csd2_bytes, JNI_ABORT);
    }
    
    bool result = InitMediaCodecDecoder(handle, width, height, frame_rate, color_format,
                                       csd0_data, csd0_size, csd1_data, csd1_size, csd2_data, csd2_size);
    
    if (csd0_data) delete[] csd0_data;
    if (csd1_data) delete[] csd1_data;
    if (csd2_data) delete[] csd2_data;
    
    return result ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_startMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    bool result = StartMediaCodecDecoder(handle);
    return result ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_decodeMediaCodecFrame(
    JNIEnv* env, jobject thiz,
    jlong decoder_id,
    jbyteArray frame_data,
    jlong presentation_time_us,
    jint flags) {
    
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    
    jsize frame_size = env->GetArrayLength(frame_data);
    jbyte* frame_bytes = env->GetByteArrayElements(frame_data, nullptr);
    
    bool result = DecodeMediaCodecFrame(handle, 
                                       reinterpret_cast<uint8_t*>(frame_bytes), 
                                       frame_size,
                                       presentation_time_us,
                                       flags);
    
    env->ReleaseByteArrayElements(frame_data, frame_bytes, JNI_ABORT);
    return result ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jbyteArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getDecodedFrameYUV(
    JNIEnv* env, jobject thiz,
    jlong decoder_id,
    jint timeout_us,
    jintArray dimensions) {
    
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    
    uint8_t* yuv_data = nullptr;
    int yuv_size = 0;
    int width = 0, height = 0;
    
    bool result = GetDecodedFrameYUV(handle, &yuv_data, &yuv_size, &width, &height, timeout_us);
    
    if (!result || !yuv_data || yuv_size <= 0) {
        if (yuv_data) free(yuv_data);
        return nullptr;
    }
    
    // 设置维度
    jint dims[2] = {width, height};
    env->SetIntArrayRegion(dimensions, 0, 2, dims);
    
    // 创建返回数组
    jbyteArray resultArray = env->NewByteArray(yuv_size);
    env->SetByteArrayRegion(resultArray, 0, yuv_size, reinterpret_cast<const jbyte*>(yuv_data));
    
    free(yuv_data);
    return resultArray;
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_stopMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    bool result = StopMediaCodecDecoder(handle);
    return result ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    DestroyMediaCodecDecoder(handle);
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isMediaCodecSupported(
    JNIEnv* env, jobject thiz,
    jint codec_type) {
    MediaCodecType type = static_cast<MediaCodecType>(codec_type);
    bool supported = IsMediaCodecSupported(type);
    return supported ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getMediaCodecDeviceInfo(
    JNIEnv* env, jobject thiz) {
    const char* info = GetMediaCodecDeviceInfo();
    return env->NewStringUTF(info);
}

JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getDecoderStatus(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    DecoderStatus status = GetMediaCodecDecoderStatus(handle);
    return static_cast<jint>(status);
}

JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_flushMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    MediaCodecDecoderHandle handle = reinterpret_cast<MediaCodecDecoderHandle>(decoder_id);
    bool result = FlushMediaCodecDecoder(handle);
    return result ? JNI_TRUE : JNI_FALSE;
}

} // extern "C" 结束

// ========== 单例 Oboe Audio C接口 (保持向后兼容) ==========

bool initOboeAudio(int sample_rate, int channel_count) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->Initialize(sample_rate, channel_count);
}

bool initOboeAudioWithFormat(int sample_rate, int channel_count, int sample_format) {
    if (!g_singleton_renderer) {
        g_singleton_renderer = new RyujinxOboe::OboeAudioRenderer();
    }
    return g_singleton_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeAudio() {
    if (g_singleton_renderer) {
        g_singleton_renderer->Shutdown();
        delete g_singleton_renderer;
        g_singleton_renderer = nullptr;
    }
}

bool writeOboeAudio(const int16_t* data, int32_t num_frames) {
    return g_singleton_renderer && data && num_frames > 0 && g_singleton_renderer->WriteAudio(data, num_frames);
}

bool writeOboeAudioRaw(const uint8_t* data, int32_t num_frames, int32_t sample_format) {
    return g_singleton_renderer && data && num_frames > 0 && g_singleton_renderer->WriteAudioRaw(data, num_frames, sample_format);
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

// ========== 多实例 Oboe Audio C接口 ==========

void* createOboeRenderer() {
    return new RyujinxOboe::OboeAudioRenderer();
}

void destroyOboeRenderer(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
        delete oboe_renderer;
    }
}

bool initOboeRenderer(void* renderer, int sample_rate, int channel_count, int sample_format) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->InitializeWithFormat(sample_rate, channel_count, sample_format);
}

void shutdownOboeRenderer(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Shutdown();
    }
}

bool writeOboeRendererAudio(void* renderer, const int16_t* data, int32_t num_frames) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && data && num_frames > 0 && oboe_renderer->WriteAudio(data, num_frames);
}

bool writeOboeRendererAudioRaw(void* renderer, const uint8_t* data, int32_t num_frames, int32_t sample_format) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && data && num_frames > 0 && oboe_renderer->WriteAudioRaw(data, num_frames, sample_format);
}

void setOboeRendererVolume(void* renderer, float volume) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->SetVolume(volume);
    }
}

bool isOboeRendererInitialized(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsInitialized();
}

bool isOboeRendererPlaying(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer && oboe_renderer->IsPlaying();
}

int32_t getOboeRendererBufferedFrames(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    return oboe_renderer ? static_cast<int32_t>(oboe_renderer->GetBufferedFrames()) : 0;
}

void resetOboeRenderer(void* renderer) {
    auto oboe_renderer = reinterpret_cast<RyujinxOboe::OboeAudioRenderer*>(renderer);
    if (oboe_renderer) {
        oboe_renderer->Reset();
    }
}

const char* GetAndroidDeviceModel() {
    static char model[PROP_VALUE_MAX] = {0};
    if (model[0] == '\0') __system_property_get("ro.product.model", model);
    return model;
}

const char* GetAndroidDeviceBrand() {
    static char brand[PROP_VALUE_MAX] = {0};
    if (brand[0] == '\0') __system_property_get("ro.product.brand", brand);
    return brand;
}