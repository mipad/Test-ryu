#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <atomic>
#include <memory>
#include <cstdint>
#include "LockFreeQueue.h"

namespace RyujinxOboe {

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

struct AudioBlock {
    uint8_t* data = nullptr;
    size_t data_size = 0;
    size_t data_used = 0;
    int32_t sample_format = PCM_INT16;
    
    AudioBlock() = default;
    
    explicit AudioBlock(size_t size) {
        data = new uint8_t[size];
        data_size = size;
        data_used = 0;
    }
    
    ~AudioBlock() {
        if (data) {
            delete[] data;
        }
    }
    
    void clear() {
        data_used = 0;
    }
    
    size_t available() const {
        return data_size - data_used;
    }
    
    // 禁止拷贝
    AudioBlock(const AudioBlock&) = delete;
    AudioBlock& operator=(const AudioBlock&) = delete;
    
    // 允许移动
    AudioBlock(AudioBlock&& other) noexcept {
        data = other.data;
        data_size = other.data_size;
        data_used = other.data_used;
        sample_format = other.sample_format;
        other.data = nullptr;
        other.data_size = 0;
        other.data_used = 0;
    }
    
    AudioBlock& operator=(AudioBlock&& other) noexcept {
        if (this != &other) {
            if (data) {
                delete[] data;
            }
            data = other.data;
            data_size = other.data_size;
            data_used = other.data_used;
            sample_format = other.sample_format;
            other.data = nullptr;
            other.data_size = 0;
            other.data_used = 0;
        }
        return *this;
    }
};

class OboeAudioRenderer {
public:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    bool InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat);
    void Shutdown();
    
    bool WriteAudio(const int16_t* data, int32_t num_frames);
    bool WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat);
    
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { return m_stream && m_stream->getState() == oboe::StreamState::Started; }
    int32_t GetBufferedFrames() const;
    
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }

    void Reset();

private:
    class AAudioExclusiveCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit AAudioExclusiveCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    class AAudioExclusiveErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit AAudioExclusiveErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    oboe::AudioFormat MapSampleFormat(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    bool OptimizeBufferSize();
    bool TryOpenStreamWithRetry(int maxRetryCount = 3);
    
    size_t CalculateOptimalBlockSize() const;
    std::unique_ptr<AudioBlock> CreateAudioBlock(size_t size);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    int32_t m_frames_per_burst{256};
    
    // 动态调整的无锁队列
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 128; // 较小的队列大小
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    
    // 当前正在播放的块
    std::unique_ptr<AudioBlock> m_current_block;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H