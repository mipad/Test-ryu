#ifndef MEDIACODEC_COMMON_H
#define MEDIACODEC_COMMON_H

#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>
#include <android/native_window.h>
#include <vector>
#include <string>
#include <memory>
#include <atomic>
#include <mutex>
#include <condition_variable>

namespace RyujinxMediaCodec {

// 解码器状态
enum class DecoderStatus {
    UNINITIALIZED = 0,
    INITIALIZED,
    RUNNING,
    STOPPED,
    ERROR
};

// 视频编解码器类型
enum class VideoCodec {
    H264,
    VP8,
    VP9,
    HEVC,
    AV1
};

// 颜色格式
enum class ColorFormat {
    YUV420_PLANAR = 0x13,        // YV12
    YUV420_SEMIPLANAR = 0x15,    // NV12
    YUV420_PACKED_SEMIPLANAR = 0x27, // NV21
    YUV420_FLEXIBLE = 0x7F420888
};

// 解码帧信息
struct DecodedFrame {
    std::vector<uint8_t> yData;
    std::vector<uint8_t> uData;
    std::vector<uint8_t> vData;
    int width = 0;
    int height = 0;
    int64_t presentationTimeUs = 0;
    int flags = 0;
    bool isKeyFrame = false;
};

// 解码器配置
struct DecoderConfig {
    VideoCodec codec = VideoCodec::H264;
    int width = 0;
    int height = 0;
    int frameRate = 30;
    int bitrate = 0;
    int iFrameInterval = 1;
    ColorFormat colorFormat = ColorFormat::YUV420_SEMIPLANAR;
    bool useSurface = false;
    ANativeWindow* surface = nullptr;
    std::vector<uint8_t> csd0; // Codec Specific Data (SPS for H.264)
    std::vector<uint8_t> csd1; // Codec Specific Data (PPS for H.264)
    std::vector<uint8_t> csd2; // Codec Specific Data (VP9 header)
};

// 帧回调接口
class FrameCallback {
public:
    virtual ~FrameCallback() = default;
    virtual void OnFrameDecoded(const DecodedFrame& frame) = 0;
    virtual void OnError(const std::string& error) = 0;
    virtual void OnFormatChanged(int width, int height, int colorFormat) = 0;
};

// 解码器统计信息
struct DecoderStats {
    uint64_t framesDecoded = 0;
    uint64_t framesDropped = 0;
    uint64_t bytesProcessed = 0;
    double averageDecodeTimeMs = 0.0;
    uint64_t lastFrameTimestamp = 0;
};

// 辅助函数
class MediaCodecUtils {
public:
    // 获取 MIME 类型
    static std::string GetMimeType(VideoCodec codec);
    
    // 检查编解码器支持
    static bool IsCodecSupported(VideoCodec codec);
    
    // 获取最佳解码器名称
    static std::string GetBestDecoderName(VideoCodec codec);
    
    // 获取设备信息
    static std::string GetDeviceInfo();
    
    // 获取支持的编解码器列表
    static std::vector<std::string> GetSupportedCodecs();
    
    // 时间戳转换
    static int64_t SystemTimeToPresentationTimeUs();
    static int64_t MillisecondsToMicroseconds(int64_t ms);
    
    // 错误代码转字符串
    static std::string ErrorToString(media_status_t status);
    
    // YUV 格式检查
    static bool IsYUVFormatSupported(ColorFormat format);
    
    // 计算 YUV 缓冲区大小
    static size_t CalculateYUVSize(int width, int height, ColorFormat format);
};

} // namespace RyujinxMediaCodec

#endif // MEDIACODEC_COMMON_H