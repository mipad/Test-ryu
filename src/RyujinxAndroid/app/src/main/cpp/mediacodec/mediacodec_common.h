#ifndef MEDIACODEC_DECODER_H
#define MEDIACODEC_DECODER_H

#include "mediacodec_common.h"
#include <functional>
#include <queue>
#include <thread>

namespace RyujinxMediaCodec {

// 解码器基类接口
class IMediaCodecDecoder {
public:
    virtual ~IMediaCodecDecoder() = default;
    
    // 初始化
    virtual bool Initialize(const DecoderConfig& config) = 0;
    
    // 启动解码器
    virtual bool Start() = 0;
    
    // 停止解码器
    virtual bool Stop() = 0;
    
    // 重启解码器
    virtual bool Restart() = 0;
    
    // 解码帧
    virtual bool DecodeFrame(const uint8_t* data, size_t size, 
                            int64_t presentationTimeUs = 0, 
                            int flags = 0) = 0;
    
    // 获取解码后的帧
    virtual bool GetDecodedFrame(DecodedFrame& frame, int timeoutUs = 10000) = 0;
    
    // 获取解码后的 YUV 数据
    virtual bool GetYUVData(std::vector<uint8_t>& yuvData, 
                           int& width, int& height,
                           int timeoutUs = 10000) = 0;
    
    // 刷新解码器
    virtual bool Flush() = 0;
    
    // 释放资源
    virtual void Release() = 0;
    
    // 状态查询
    virtual DecoderStatus GetStatus() const = 0;
    virtual bool IsInitialized() const = 0;
    virtual bool IsRunning() const = 0;
    
    // 统计信息
    virtual DecoderStats GetStats() const = 0;
    virtual void ResetStats() = 0;
    
    // 设置回调
    virtual void SetFrameCallback(FrameCallback* callback) = 0;
    virtual void RemoveFrameCallback() = 0;
    
    // 配置更新
    virtual bool UpdateConfig(const DecoderConfig& config) = 0;
};

// 通用的 MediaCodec 解码器实现
class MediaCodecDecoder : public IMediaCodecDecoder {
public:
    MediaCodecDecoder();
    virtual ~MediaCodecDecoder();
    
    // IMediaCodecDecoder 接口实现
    bool Initialize(const DecoderConfig& config) override;
    bool Start() override;
    bool Stop() override;
    bool Restart() override;
    bool DecodeFrame(const uint8_t* data, size_t size,
                    int64_t presentationTimeUs = 0,
                    int flags = 0) override;
    bool GetDecodedFrame(DecodedFrame& frame, int timeoutUs = 10000) override;
    bool GetYUVData(std::vector<uint8_t>& yuvData,
                   int& width, int& height,
                   int timeoutUs = 10000) override;
    bool Flush() override;
    void Release() override;
    
    DecoderStatus GetStatus() const override { return status_; }
    bool IsInitialized() const override { return status_ >= DecoderStatus::INITIALIZED; }
    bool IsRunning() const override { return status_ == DecoderStatus::RUNNING; }
    
    DecoderStats GetStats() const override { return stats_; }
    void ResetStats() override;
    
    void SetFrameCallback(FrameCallback* callback) override;
    void RemoveFrameCallback() override;
    
    bool UpdateConfig(const DecoderConfig& config) override;
    
protected:
    // 子类可以重写的虚函数
    virtual bool ConfigureMediaFormat(AMediaFormat* format, const DecoderConfig& config);
    virtual bool ProcessOutputBuffer(size_t bufferIndex, 
                                    const AMediaCodecBufferInfo& info);
    virtual bool ExtractFrameFromBuffer(size_t bufferIndex,
                                       const AMediaCodecBufferInfo& info,
                                       DecodedFrame& frame);
    virtual void HandleOutputFormatChanged(AMediaFormat* format);
    
private:
    // 内部实现
    bool InitializeInternal();
    void CleanupInternal();
    bool ConfigureCodec();
    void OutputThread();
    bool ProcessOutput(int timeoutUs);
    bool ExtractYUVData(size_t bufferIndex,
                       const AMediaCodecBufferInfo& info,
                       DecodedFrame& frame);
    void UpdateStats(bool success, size_t bytes, int64_t decodeTime);
    
private:
    std::mutex mutex_;
    std::condition_variable condition_;
    
    AMediaCodec* mediaCodec_ = nullptr;
    AMediaFormat* mediaFormat_ = nullptr;
    ANativeWindow* outputSurface_ = nullptr;
    
    DecoderConfig config_;
    DecoderStatus status_ = DecoderStatus::UNINITIALIZED;
    std::atomic<bool> running_{false};
    
    // 输出队列
    std::queue<DecodedFrame> decodedFrames_;
    size_t maxFrames_ = 5; // 最大帧缓存数
    
    // 回调
    FrameCallback* frameCallback_ = nullptr;
    
    // 工作线程
    std::thread outputThread_;
    
    // 统计
    DecoderStats stats_;
    std::chrono::steady_clock::time_point lastStatTime_;
    
    // 输出格式信息
    int outputWidth_ = 0;
    int outputHeight_ = 0;
    int outputColorFormat_ = 0;
    
    // 时间戳管理
    int64_t frameCount_ = 0;
    int64_t lastPresentationTimeUs_ = 0;
    
    // 错误信息
    std::string lastError_;
};

} // namespace RyujinxMediaCodec

#endif // MEDIACODEC_DECODER_H
