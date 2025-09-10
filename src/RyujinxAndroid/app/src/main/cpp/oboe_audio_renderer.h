// oboe_audio_renderer.h (终极修复版：配合高质量采样率转换 + 动态缓冲监控)
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>

// =============== 环形缓冲区 ===============
class RingBuffer {
private:
    std::vector<float> mBuffer;
    std::atomic<size_t> mReadIndex{0};
    std::atomic<size_t> mWriteIndex{0};
    size_t mCapacity;
    mutable std::mutex mMutex;

public:
    explicit RingBuffer(size_t capacity);
    bool write(const float* data, size_t count);
    size_t read(float* output, size_t count);
    size_t available() const;
    size_t availableForWrite() const;
    void clear();
};

// =============== 噪声整形器 (稳定一阶版) ===============
class NoiseShaper {
private:
    float mHistory[3] = {0}; // 初始化为0
    mutable std::mutex mMutex;

public:
    NoiseShaper() = default;
    void reset();
    float process(float input);
};

// =============== 高质量采样率转换器 (Cubic插值) ===============
class SampleRateConverter {
private:
    float mLastSamples[4] = {0};
    float mPosition = 0.0f;
    float mRatio = 1.0f;
    size_t mWriteIndex = 0;
    bool mHasEnoughSamples = false;

    static float cubicInterpolate(float y0, float y1, float y2, float y3, float mu);

public:
    SampleRateConverter() = default;
    void setRatio(float inputRate, float outputRate);
    void reset();
    size_t convert(const float* input, size_t inputSize, float* output, size_t outputSize);
};

// =============== Oboe 音频渲染器 ===============
class OboeAudioRenderer : public oboe::AudioStreamDataCallback,
                          public oboe::AudioStreamErrorCallback {
public:
    static OboeAudioRenderer& getInstance();

    bool initialize();
    void shutdown();

    void setSampleRate(int32_t sampleRate);
    void setBufferSize(int32_t bufferSize);
    void setVolume(float volume);
    void setNoiseShapingEnabled(bool enabled);
    void setChannelCount(int32_t channelCount);

    void writeAudio(const float* data, int32_t numFrames, int32_t inputChannels);
    void clearBuffer();

    // 状态查询
    bool isInitialized() const { return mIsInitialized.load(std::memory_order_acquire); }
    int32_t getSampleRate() const { return mSampleRate.load(std::memory_order_relaxed); }
    int32_t getBufferSize() const { return mBufferSize.load(std::memory_order_relaxed); }
    int32_t getChannelCount() const { return mChannelCount.load(std::memory_order_relaxed); }
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
    void convertChannels(const float* input, float* output, int32_t numFrames, int32_t inputChannels, int32_t outputChannels);

    std::shared_ptr<oboe::AudioStream> mAudioStream;
    std::unique_ptr<RingBuffer> mRingBuffer;
    std::unique_ptr<NoiseShaper> mNoiseShaper;
    std::unique_ptr<SampleRateConverter> mSampleRateConverter;
    std::mutex mInitMutex;

    // 状态标志
    std::atomic<bool> mIsInitialized{false};
    std::atomic<bool> mIsStreamStarted{false};
    std::atomic<bool> mNoiseShapingEnabled{true};

    // 音频参数
    std::atomic<int32_t> mSampleRate{48000};
    std::atomic<int32_t> mBufferSize{1024};
    std::atomic<int32_t> mChannelCount{2};
    std::atomic<float> mVolume{1.0f};
    std::atomic<oboe::AudioFormat> mAudioFormat{oboe::AudioFormat::Float};

    // 动态缓冲区监控
    std::atomic<size_t> mLastBufferLevel{0};
    std::atomic<size_t> mUnderrunCount{0};
    std::atomic<size_t> mTotalFramesWritten{0};
};

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
