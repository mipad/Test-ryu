// oboe_audio_renderer.h (修复版)
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>

// 环形缓冲区（使用互斥锁保证线程安全）
class RingBuffer {
private:
    std::vector<float> mBuffer;
    std::atomic<size_t> mReadIndex{0};
    std::atomic<size_t> mWriteIndex{0};
    size_t mCapacity;
    mutable std::mutex mMutex; // 添加互斥锁成员变量

public:
    explicit RingBuffer(size_t capacity);
    bool write(const float* data, size_t count);
    size_t read(float* output, size_t count);
    size_t available() const;
    size_t availableForWrite() const;
    void clear();
};

// 前向声明
class NoiseShaper;
class SampleRateConverter;

class OboeAudioRenderer : public oboe::AudioStreamDataCallback,
                          public oboe::AudioStreamErrorCallback {
public:
    static OboeAudioRenderer& getInstance();

    bool initialize();
    void shutdown();

    void setSampleRate(int32_t sampleRate);
    void setBufferSize(int32_t bufferSize);
    void setVolume(float volume);

    void writeAudio(const float* data, int32_t numFrames);
    void clearBuffer();

    // 状态查询
    bool isInitialized() const { return mIsInitialized.load(std::memory_order_acquire); }
    int32_t getSampleRate() const { return mSampleRate.load(std::memory_order_relaxed); }
    int32_t getBufferSize() const { return mBufferSize.load(std::memory_order_relaxed); }
    size_t getBufferedFrames() const;
    size_t getAvailableFrames() const;

    // Oboe 回调
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) override;
    void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    bool openStreamWithFormat(oboe::AudioFormat format);
    void updateStreamParameters();

    std::shared_ptr<oboe::AudioStream> mAudioStream;
    std::unique_ptr<RingBuffer> mRingBuffer;
    std::unique_ptr<NoiseShaper> mNoiseShaper;
    std::unique_ptr<SampleRateConverter> mSampleRateConverter;
    std::mutex mInitMutex;
    std::atomic<bool> mIsInitialized{false};
    std::atomic<bool> mIsStreamStarted{false}; // ✅ 新增：流是否已启动
    std::atomic<int32_t> mSampleRate{48000};
    std::atomic<int32_t> mBufferSize{1024};
    std::atomic<int32_t> mChannelCount{2};
    std::atomic<float> mVolume{1.0f};
    std::atomic<oboe::AudioFormat> mAudioFormat{oboe::AudioFormat::Float};
};

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
