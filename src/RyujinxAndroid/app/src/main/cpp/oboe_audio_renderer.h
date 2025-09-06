// oboe_audio_renderer.h
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>

// 无锁环形缓冲区（单生产者单消费者模型）
class RingBuffer {
private:
    std::vector<float> mBuffer;
    std::atomic<size_t> mReadIndex{0};
    std::atomic<size_t> mWriteIndex{0};
    size_t mCapacity;

public:
    explicit RingBuffer(size_t capacity);
    bool write(const float* data, size_t count);
    size_t read(float* output, size_t count);
    size_t available() const;
    void clear();
};

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

    // 状态查询（调试用）
    bool isInitialized() const { return mIsInitialized.load(std::memory_order_acquire); }
    int32_t getSampleRate() const { return mSampleRate.load(std::memory_order_relaxed); }
    int32_t getBufferSize() const { return mBufferSize.load(std::memory_order_relaxed); }
    size_t getBufferedFrames() const;

    // oboe::AudioStreamDataCallback
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) override;

    // oboe::AudioStreamErrorCallback
    void onErrorAfterClose(oboee::AudioStream* audioStream, oboe::Result error) override;
    void onErrorBeforeClose(oboee::AudioStream* audioStream, oboe::Result error) override;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    std::shared_ptr<oboe::AudioStream> mAudioStream;
    std::unique_ptr<RingBuffer> mRingBuffer;
    std::mutex mInitMutex; // 防止 initialize 重入
    std::atomic<bool> mIsInitialized{false};
    std::atomic<int32_t> mSampleRate{48000};
    std::atomic<int32_t> mBufferSize{1024};
    const int32_t mChannelCount = 2; // 固定立体声
    std::atomic<float> mVolume{1.0f};
};

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
