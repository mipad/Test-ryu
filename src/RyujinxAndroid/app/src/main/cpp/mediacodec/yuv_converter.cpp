#include "mediacodec_common.h"
#include <cstring>

namespace RyujinxMediaCodec {

class YUVConverter {
public:
    // NV12 转 YV12/YU12
    static bool NV12ToYUV420Planar(const uint8_t* nv12, int width, int height,
                                  std::vector<uint8_t>& yuv420) {
        int ySize = width * height;
        int uvSize = ySize / 4;
        
        yuv420.resize(ySize + uvSize * 2);
        uint8_t* y = yuv420.data();
        uint8_t* u = y + ySize;
        uint8_t* v = u + uvSize;
        
        // 复制 Y 分量
        memcpy(y, nv12, ySize);
        
        // 分离 UV 分量
        const uint8_t* uv = nv12 + ySize;
        for (int i = 0; i < uvSize; i += 2) {
            *u++ = uv[i];     // U
            *v++ = uv[i + 1]; // V
        }
        
        return true;
    }
    
    // YV12/YU12 转 NV12
    static bool YUV420PlanarToNV12(const uint8_t* y, const uint8_t* u, const uint8_t* v,
                                  int width, int height, std::vector<uint8_t>& nv12) {
        int ySize = width * height;
        int uvSize = ySize / 2;
        
        nv12.resize(ySize + uvSize);
        uint8_t* dstY = nv12.data();
        uint8_t* dstUV = dstY + ySize;
        
        // 复制 Y 分量
        memcpy(dstY, y, ySize);
        
        // 合并 UV 分量
        for (int i = 0; i < uvSize / 2; i++) {
            dstUV[i * 2] = u[i];     // U
            dstUV[i * 2 + 1] = v[i]; // V
        }
        
        return true;
    }
    
    // NV21 转 NV12
    static bool NV21ToNV12(const uint8_t* nv21, int width, int height,
                          std::vector<uint8_t>& nv12) {
        int ySize = width * height;
        int uvSize = ySize / 2;
        
        nv12.resize(ySize + uvSize);
        
        // 复制 Y 分量
        memcpy(nv12.data(), nv21, ySize);
        
        // 交换 UV 分量
        const uint8_t* vu = nv21 + ySize;
        uint8_t* uv = nv12.data() + ySize;
        
        for (int i = 0; i < uvSize; i += 2) {
            uv[i] = vu[i + 1];     // V -> U
            uv[i + 1] = vu[i];     // U -> V
        }
        
        return true;
    }
    
    // 调整 YUV 大小（简单缩放）
    static bool ResizeYUV(const uint8_t* srcY, const uint8_t* srcU, const uint8_t* srcV,
                         int srcWidth, int srcHeight,
                         uint8_t* dstY, uint8_t* dstU, uint8_t* dstV,
                         int dstWidth, int dstHeight) {
        // 简单实现：最近邻缩放
        float scaleX = static_cast<float>(srcWidth) / dstWidth;
        float scaleY = static_cast<float>(srcHeight) / dstHeight;
        
        // 缩放 Y 分量
        for (int y = 0; y < dstHeight; y++) {
            int srcYIdx = static_cast<int>(y * scaleY) * srcWidth;
            int dstYIdx = y * dstWidth;
            
            for (int x = 0; x < dstWidth; x++) {
                int srcX = static_cast<int>(x * scaleX);
                dstY[dstYIdx + x] = srcY[srcYIdx + srcX];
            }
        }
        
        // 缩放 UV 分量
        int srcUVWidth = srcWidth / 2;
        int srcUVHeight = srcHeight / 2;
        int dstUVWidth = dstWidth / 2;
        int dstUVHeight = dstHeight / 2;
        
        for (int y = 0; y < dstUVHeight; y++) {
            int srcYIdx = static_cast<int>(y * scaleY) * srcUVWidth;
            int dstYIdx = y * dstUVWidth;
            
            for (int x = 0; x < dstUVWidth; x++) {
                int srcX = static_cast<int>(x * scaleX);
                dstU[dstYIdx + x] = srcU[srcYIdx + srcX];
                dstV[dstYIdx + x] = srcV[srcYIdx + srcX];
            }
        }
        
        return true;
    }
};

} // namespace RyujinxMediaCodec