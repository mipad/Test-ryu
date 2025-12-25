#include "mediacodec_decoder.h"
#include <vector>

namespace RyujinxMediaCodec {

// H.264 特定的解码器
class MediaCodecH264Decoder : public MediaCodecDecoder {
public:
    MediaCodecH264Decoder() = default;
    virtual ~MediaCodecH264Decoder() = default;
    
protected:
    bool ConfigureMediaFormat(AMediaFormat* format, const DecoderConfig& config) override {
        // 调用基类配置
        if (!MediaCodecDecoder::ConfigureMediaFormat(format, config)) {
            return false;
        }
        
        // H.264 特定配置
        if (!config.csd0.empty()) {
            // 设置 SPS
            AMediaFormat_setBuffer(format, "csd-0", 
                                  config.csd0.data(), config.csd0.size());
            
            // 解析 SPS 获取 profile 和 level
            int profile = ExtractH264Profile(config.csd0);
            int level = ExtractH264Level(config.csd0);
            
            if (profile > 0) {
                AMediaFormat_setInt32(format, "profile", profile);
            }
            if (level > 0) {
                AMediaFormat_setInt32(format, "level", level);
            }
        }
        
        if (!config.csd1.empty()) {
            // 设置 PPS
            AMediaFormat_setBuffer(format, "csd-1", 
                                  config.csd1.data(), config.csd1.size());
        }
        
        // 设置 H.264 特定参数
        AMediaFormat_setInt32(format, "max-width", config.width);
        AMediaFormat_setInt32(format, "max-height", config.height);
        
        return true;
    }
    
private:
    int ExtractH264Profile(const std::vector<uint8_t>& sps) {
        // 简单解析 SPS 获取 profile_idc
        if (sps.size() < 4) return 0;
        
        // 跳过 NAL 头 (0x00 0x00 0x00 0x01 或 0x00 0x00 0x01)
        size_t offset = 0;
        while (offset < sps.size() - 1 && 
               (sps[offset] != 0x01 || sps[offset-1] != 0x00)) {
            offset++;
        }
        
        if (offset >= sps.size() - 1) return 0;
        
        // NAL 单元类型 (profile_idc 在偏移 1 字节处)
        offset++; // 跳过 0x01
        if (offset < sps.size()) {
            return sps[offset]; // profile_idc
        }
        
        return 0;
    }
    
    int ExtractH264Level(const std::vector<uint8_t>& sps) {
        // 解析 SPS 获取 level_idc
        if (sps.size() < 5) return 0;
        
        size_t offset = 0;
        while (offset < sps.size() - 1 && 
               (sps[offset] != 0x01 || sps[offset-1] != 0x00)) {
            offset++;
        }
        
        if (offset >= sps.size() - 2) return 0;
        
        offset++; // 跳过 0x01
        offset++; // 跳过 profile_idc
        
        if (offset < sps.size()) {
            return sps[offset]; // level_idc
        }
        
        return 0;
    }
};

} // namespace RyujinxMediaCodec