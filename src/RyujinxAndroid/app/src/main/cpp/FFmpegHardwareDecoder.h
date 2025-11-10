#ifndef FFMPEG_HARDWARE_DECODER_H
#define FFMPEG_HARDWARE_DECODER_H

#include <jni.h>
#include <unordered_map>
#include <string>
#include <vector>

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

// 硬件解码上下文结构
struct HardwareDecoderContext {
    AVCodecContext* codec_ctx;
    AVBufferRef* hw_device_ctx;
    AVFrame* hw_frame;
    AVFrame* sw_frame;
    enum AVPixelFormat hw_pix_fmt;
    bool initialized;
    
    HardwareDecoderContext() 
        : codec_ctx(nullptr)
        , hw_device_ctx(nullptr)
        , hw_frame(nullptr)
        , sw_frame(nullptr)
        , hw_pix_fmt(AV_PIX_FMT_NONE)
        , initialized(false) {}
};

class FFmpegHardwareDecoder {
public:
    static FFmpegHardwareDecoder& GetInstance();
    
    // 初始化硬件解码器
    bool Initialize();
    
    // 清理资源
    void Cleanup();
    
    // 硬件解码器管理
    jlong CreateHardwareDecoderContext(JNIEnv* env, const char* codecName);
    bool DestroyHardwareDecoderContext(jlong contextId);
    
    // 解码功能
    int DecodeVideoFrame(jlong contextId, jbyteArray inputData, jint inputSize,
                        jintArray frameInfo, jobjectArray planeData);
    
    // 工具函数
    bool IsHardwareDecoderSupported(const char* decoderType);
    const char* GetHardwareDecoderName(const char* codecName);
    bool IsHardwareDecoderAvailable(const char* codecName);
    int GetHardwarePixelFormat(const char* decoderName);
    std::vector<std::string> GetSupportedHardwareDecoders();
    std::vector<std::string> GetHardwareDeviceTypes();
    jlong InitHardwareDeviceContext(const char* deviceType);
    void FreeHardwareDeviceContext(jlong deviceCtxPtr);
    void FlushDecoder(jlong contextId);
    const char* GetFFmpegVersion();
    
private:
    FFmpegHardwareDecoder();
    ~FFmpegHardwareDecoder();
    
    // 禁用拷贝构造和赋值
    FFmpegHardwareDecoder(const FFmpegHardwareDecoder&) = delete;
    FFmpegHardwareDecoder& operator=(const FFmpegHardwareDecoder&) = delete;
    
    bool InitializeHardwareDecoder(jlong contextId, const char* decoderName, jlong deviceCtxPtr);
    bool TransferHardwareFrameToSoftware(HardwareDecoderContext* ctx);
    
private:
    std::unordered_map<jlong, HardwareDecoderContext*> decoderContexts_;
    jlong nextContextId_;
    bool initialized_;
    
    // 硬件解码器类型映射
    static const std::unordered_map<std::string, std::string> HARDWARE_DECODERS;
};

#endif // FFMPEG_HARDWARE_DECODER_H