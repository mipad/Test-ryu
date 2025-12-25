#include "mediacodec_decoder.h"

namespace RyujinxMediaCodec {

// VP8 特定的解码器
class MediaCodecVP8Decoder : public MediaCodecDecoder {
public:
    MediaCodecVP8Decoder() = default;
    virtual ~MediaCodecVP8Decoder() = default;
    
protected:
    bool ConfigureMediaFormat(AMediaFormat* format, const DecoderConfig& config) override {
        // 调用基类配置
        if (!MediaCodecDecoder::ConfigureMediaFormat(format, config)) {
            return false;
        }
        
        // VP8 特定配置
        if (!config.csd0.empty()) {
            // VP8 的 CSD 数据通常是帧头
            AMediaFormat_setBuffer(format, "csd-0", 
                                  config.csd0.data(), config.csd0.size());
        }
        
        // 设置 VP8 特定参数
        AMediaFormat_setInt32(format, "max-width", config.width);
        AMediaFormat_setInt32(format, "max-height", config.height);
        
        return true;
    }
};

} // namespace RyujinxMediaCodec