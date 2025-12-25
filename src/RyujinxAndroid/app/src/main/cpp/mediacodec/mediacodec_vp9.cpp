#include "mediacodec_decoder.h"

namespace RyujinxMediaCodec {

// VP9 特定的解码器
class MediaCodecVP9Decoder : public MediaCodecDecoder {
public:
    MediaCodecVP9Decoder() = default;
    virtual ~MediaCodecVP9Decoder() = default;
    
protected:
    bool ConfigureMediaFormat(AMediaFormat* format, const DecoderConfig& config) override {
        // 调用基类配置
        if (!MediaCodecDecoder::ConfigureMediaFormat(format, config)) {
            return false;
        }
        
        // VP9 特定配置
        if (!config.csd0.empty()) {
            // VP9 的 CSD 数据
            AMediaFormat_setBuffer(format, "csd-0", 
                                  config.csd0.data(), config.csd0.size());
        }
        
        if (!config.csd1.empty()) {
            AMediaFormat_setBuffer(format, "csd-1", 
                                  config.csd1.data(), config.csd1.size());
        }
        
        if (!config.csd2.empty()) {
            AMediaFormat_setBuffer(format, "csd-2", 
                                  config.csd2.data(), config.csd2.size());
        }
        
        // 设置 VP9 特定参数
        AMediaFormat_setInt32(format, "max-width", config.width);
        AMediaFormat_setInt32(format, "max-height", config.height);
        
        // VP9 配置文件
        int profile = ExtractVP9Profile(config);
        if (profile >= 0) {
            AMediaFormat_setInt32(format, "profile", profile);
        }
        
        return true;
    }
    
private:
    int ExtractVP9Profile(const DecoderConfig& config) {
        // 简单解析 VP9 帧头获取 profile
        // 实际实现需要解析 VP9 帧头
        if (!config.csd0.empty() && config.csd0.size() >= 1) {
            uint8_t firstByte = config.csd0[0];
            // VP9 帧头中 profile 在特定位置
            // 这里简化处理，实际需要完整解析
            return (firstByte & 0xE0) >> 5; // 假设 profile 在 bits 5-7
        }
        return -1;
    }
};

} // namespace RyujinxMediaCodec