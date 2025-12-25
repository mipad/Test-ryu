#include "mediacodec_common.h"
#include <chrono>
#include <android/log.h>
#include <sys/system_properties.h>

#define LOG_TAG "MediaCodecCommon"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

namespace RyujinxMediaCodec {

std::string MediaCodecUtils::GetMimeType(VideoCodec codec) {
    switch (codec) {
        case VideoCodec::H264:
            return "video/avc";
        case VideoCodec::VP8:
            return "video/x-vnd.on2.vp8";
        case VideoCodec::VP9:
            return "video/x-vnd.on2.vp9";
        case VideoCodec::HEVC:
            return "video/hevc";
        case VideoCodec::AV1:
            return "video/av01";
        default:
            return "video/avc";
    }
}

bool MediaCodecUtils::IsCodecSupported(VideoCodec codec) {
    std::string mimeType = GetMimeType(codec);
    
    // 尝试创建解码器来检查支持
    AMediaCodec* codecPtr = AMediaCodec_createDecoderByType(mimeType.c_str());
    if (codecPtr) {
        AMediaCodec_delete(codecPtr);
        LOGI("Codec %s is supported", mimeType.c_str());
        return true;
    }
    
    LOGW("Codec %s is not supported", mimeType.c_str());
    return false;
}

std::string MediaCodecUtils::GetBestDecoderName(VideoCodec codec) {
    std::string mimeType = GetMimeType(codec);
    
    // 实际应用中应该查询所有解码器并选择最佳的一个
    // 这里返回一个默认值
    if (codec == VideoCodec::H264) {
        return "OMX.google.h264.decoder";
    } else if (codec == VideoCodec::VP8) {
        return "OMX.google.vp8.decoder";
    } else if (codec == VideoCodec::VP9) {
        return "OMX.google.vp9.decoder";
    }
    
    return "OMX.google.h264.decoder";
}

std::string MediaCodecUtils::GetDeviceInfo() {
    char buffer[PROP_VALUE_MAX] = {0};
    
    __system_property_get("ro.product.manufacturer", buffer);
    std::string manufacturer = buffer;
    
    __system_property_get("ro.product.model", buffer);
    std::string model = buffer;
    
    __system_property_get("ro.board.platform", buffer);
    std::string platform = buffer;
    
    __system_property_get("ro.build.version.sdk", buffer);
    std::string sdk = buffer;
    
    return manufacturer + " " + model + " (Platform: " + platform + 
           ", SDK: " + sdk + ")";
}

std::vector<std::string> MediaCodecUtils::GetSupportedCodecs() {
    std::vector<std::string> codecs;
    
    // 尝试所有支持的编解码器
    VideoCodec allCodecs[] = {
        VideoCodec::H264,
        VideoCodec::VP8,
        VideoCodec::VP9,
        VideoCodec::HEVC,
        VideoCodec::AV1
    };
    
    for (auto codec : allCodecs) {
        if (IsCodecSupported(codec)) {
            codecs.push_back(GetMimeType(codec));
        }
    }
    
    return codecs;
}

int64_t MediaCodecUtils::SystemTimeToPresentationTimeUs() {
    auto now = std::chrono::steady_clock::now();
    auto duration = std::chrono::duration_cast<std::chrono::microseconds>(
        now.time_since_epoch());
    return duration.count();
}

int64_t MediaCodecUtils::MillisecondsToMicroseconds(int64_t ms) {
    return ms * 1000;
}

std::string MediaCodecUtils::ErrorToString(media_status_t status) {
    switch (status) {
        case AMEDIA_OK: return "OK";
        case AMEDIA_ERROR_BASE: return "Base error";
        case AMEDIA_ERROR_MALFORMED: return "Malformed";
        case AMEDIA_ERROR_UNSUPPORTED: return "Unsupported";
        case AMEDIA_ERROR_INVALID_OBJECT: return "Invalid object";
        case AMEDIA_ERROR_INVALID_PARAMETER: return "Invalid parameter";
        case AMEDIA_ERROR_INVALID_OPERATION: return "Invalid operation";
        case AMEDIA_ERROR_END_OF_STREAM: return "End of stream";
        case AMEDIA_ERROR_IO: return "I/O error";
        case AMEDIA_ERROR_WOULD_BLOCK: return "Would block";
        default: return "Unknown error";
    }
}

bool MediaCodecUtils::IsYUVFormatSupported(ColorFormat format) {
    int formatInt = static_cast<int>(format);
    
    // 检查常见格式
    return formatInt == 0x13 ||  // YUV420_PLANAR
           formatInt == 0x15 ||  // YUV420_SEMIPLANAR
           formatInt == 0x27 ||  // YUV420_PACKED_SEMIPLANAR
           formatInt == 0x7F420888; // YUV420_FLEXIBLE
}

size_t MediaCodecUtils::CalculateYUVSize(int width, int height, ColorFormat format) {
    int ySize = width * height;
    int uvSize = ySize / 4;
    
    switch (format) {
        case ColorFormat::YUV420_PLANAR:
            return ySize + uvSize * 2;  // Y + U + V
        
        case ColorFormat::YUV420_SEMIPLANAR:
        case ColorFormat::YUV420_PACKED_SEMIPLANAR:
            return ySize + uvSize * 2;  // Y + UV interleaved
        
        case ColorFormat::YUV420_FLEXIBLE:
            return ySize * 3 / 2;  // 通常为 1.5 倍
        
        default:
            return ySize * 3 / 2;
    }
}

} // namespace RyujinxMediaCodec