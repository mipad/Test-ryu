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
#include <sys/system_properties.h>

// MediaCodec 相关头文件
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>

#define CALL_VK(func) if (VK_SUCCESS != (func)) { assert(false); }
#define VK_CHECK(x) CALL_VK(x)
#define LoadLib(a) dlopen(a, RTLD_NOW)

// 全局变量声明
extern long _renderingThreadId;
extern JavaVM *_vm;
extern jobject _mainActivity;
extern jclass _mainActivityClass;
extern pthread_t _renderingThreadIdNative;
extern bool isInitialOrientationFlipped;

// 时间点变量
extern std::chrono::time_point<std::chrono::steady_clock, std::chrono::nanoseconds> _currentTimePoint;

// Oboe音频渲染器全局实例
static class RyujinxOboe::OboeAudioRenderer* g_singleton_renderer = nullptr;

// MediaCodec解码器相关定义
#ifdef __cplusplus
extern "C" {
#endif

// 编解码器类型枚举
typedef enum {
    MEDIACODEC_H264 = 0,
    MEDIACODEC_VP8 = 1,
    MEDIACODEC_VP9 = 2,
    MEDIACODEC_HEVC = 3,
    MEDIACODEC_AV1 = 4
} MediaCodecType;

// 颜色格式枚举
typedef enum {
    COLOR_FORMAT_YUV420_PLANAR = 0x13,        // YV12
    COLOR_FORMAT_YUV420_SEMIPLANAR = 0x15,    // NV12
    COLOR_FORMAT_YUV420_PACKED_SEMIPLANAR = 0x27, // NV21
    COLOR_FORMAT_YUV420_FLEXIBLE = 0x7F420888
} ColorFormat;

// 解码器状态枚举
typedef enum {
    DECODER_STATUS_UNINITIALIZED = 0,
    DECODER_STATUS_INITIALIZED = 1,
    DECODER_STATUS_RUNNING = 2,
    DECODER_STATUS_STOPPED = 3,
    DECODER_STATUS_ERROR = 4
} DecoderStatus;

// 解码帧结构
typedef struct {
    int width;
    int height;
    int flags;
    long long presentationTimeUs;
    uint8_t* yData;
    uint8_t* uData;
    uint8_t* vData;
    int ySize;
    int uSize;
    int vSize;
} DecodedFrame;

// 解码器句柄
typedef void* MediaCodecDecoderHandle;

// ========== 基础JNI函数 ==========
void setRenderingThread();
void setCurrentTransform(long native_window, int transform);
void debug_break(int code);
char *getStringPointer(JNIEnv *env, jstring jS);
jstring createString(JNIEnv *env, char *ch);
jstring createStringFromStdString(JNIEnv *env, std::string s);
long createSurface(long native_surface, long instance);

// ========== 单例Oboe音频接口 ==========
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

// ========== 多实例Oboe音频接口 ==========
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

// ========== 设备信息函数 ==========
const char* GetAndroidDeviceModel();
const char* GetAndroidDeviceBrand();

// ========== MediaCodec解码器函数 ==========
MediaCodecDecoderHandle CreateMediaCodecDecoder(MediaCodecType codec_type);
bool InitMediaCodecDecoder(MediaCodecDecoderHandle decoder, 
                          int width, int height, 
                          int frame_rate,
                          int color_format,
                          const uint8_t* csd0, int csd0_size,
                          const uint8_t* csd1, int csd1_size,
                          const uint8_t* csd2, int csd2_size);
bool StartMediaCodecDecoder(MediaCodecDecoderHandle decoder);
bool DecodeMediaCodecFrame(MediaCodecDecoderHandle decoder,
                          const uint8_t* frame_data, int frame_size,
                          long long presentation_time_us,
                          int flags);
bool GetDecodedFrameYUV(MediaCodecDecoderHandle decoder,
                       uint8_t** yuv_data, int* yuv_size,
                       int* width, int* height,
                       int timeout_us);
bool StopMediaCodecDecoder(MediaCodecDecoderHandle decoder);
void DestroyMediaCodecDecoder(MediaCodecDecoderHandle decoder);
bool IsMediaCodecSupported(MediaCodecType codec_type);
const char* GetMediaCodecDeviceInfo();
DecoderStatus GetMediaCodecDecoderStatus(MediaCodecDecoderHandle decoder);
bool FlushMediaCodecDecoder(MediaCodecDecoderHandle decoder);

#ifdef __cplusplus
}
#endif

#endif //RYUJINXNATIVE_RYUIJNX_H
