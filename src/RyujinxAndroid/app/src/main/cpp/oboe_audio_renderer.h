#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <functional>
#include <concepts>
#include "LockFreeQueue.h"

namespace RyujinxOboe {

enum SampleFormat : int32_t {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

struct AudioBlock {
    static constexpr size_t BLOCK_SIZE = 1024;
    
    uint8_t data[BLOCK_SIZE]{};
    size_t data_size = 0;
    size_t data_played = 0;
    int32_t sample_format = PCM_INT16;
    bool consumed = true;
    
    void clear() noexcept {
        data_size = 0;
        data_played = 0;
        consumed = true;
    }
    
    [[nodiscard]] size_t available() const noexcept {
        return data_size - data_played;
    }
};

class OboeAudioRenderer {
public:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    // 禁止拷贝和移动
    OboeAudioRenderer(const OboeAudioRenderer&) = delete;
    OboeAudioRenderer& operator=(const OboeAudioRenderer&) = delete;
    OboeAudioRenderer(OboeAudioRenderer&&) = delete;
    OboeAudioRenderer& operator=(OboeAudioRenderer&&) = delete;

    [[nodiscard]] bool Initialize(int32_t sampleRate, int32_t channelCount);
    [[nodiscard]] bool InitializeWithFormat(int32_t sampleRate, int32_t channelCount, 
                                           int32_t sampleFormat);
    void Shutdown() noexcept;
    
    [[nodiscard]] bool WriteAudio(const int16_t* data, int32_t num_frames) noexcept;
    [[nodiscard]] bool WriteAudioRaw(const void* data, int32_t num_frames, 
                                    int32_t sampleFormat) noexcept;
    
    [[nodiscard]] bool IsInitialized() const noexcept { 
        return m_initialized.load(std::memory_order_acquire); 
    }
    
    [[nodiscard]] bool IsPlaying() const noexcept { 
        return m_stream && m_stream->getState() == oboe::StreamState::Started; 
    }
    
    [[nodiscard]] int32_t GetBufferedFrames() const noexcept;
    
    void SetVolume(float volume) noexcept;
    [[nodiscard]] float GetVolume() const noexcept { 
        return m_volume.load(std::memory_order_acquire); 
    }

    void Reset() noexcept;

private:
    class AAudioExclusiveCallback final : public oboe::AudioStreamDataCallback {
    public:
        explicit AAudioExclusiveCallback(OboeAudioRenderer* renderer) noexcept 
            : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, 
                                             void* audioData, 
                                             int32_t num_frames) noexcept override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    class AAudioExclusiveErrorCallback final : public oboe::AudioStreamErrorCallback {
    public:
        explicit AAudioExclusiveErrorCallback(OboeAudioRenderer* renderer) noexcept 
            : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream* audioStream, 
                              oboe::Result error) noexcept override;
        
        void onErrorBeforeClose(oboe::AudioStream* audioStream, 
                               oboe::Result error) noexcept override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    [[nodiscard]] bool OpenStream() noexcept;
    void CloseStream() noexcept;
    [[nodiscard]] bool ConfigureAndOpenStream() noexcept;
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) const noexcept;

    [[nodiscard]] oboe::DataCallbackResult OnAudioReadyMultiFormat(
        oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) noexcept;
    
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, 
                                oboe::Result error) noexcept;
    
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, 
                                 oboe::Result error) noexcept;

    [[nodiscard]] static oboe::AudioFormat MapSampleFormat(int32_t format) noexcept;
    [[nodiscard]] static size_t GetBytesPerSample(int32_t format) noexcept;
    [[nodiscard]] bool OptimizeBufferSize() noexcept;

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    mutable std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 512;
    static constexpr uint32_t OBJECT_POOL_SIZE = 1024;
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    LockFreeObjectPool<AudioBlock, OBJECT_POOL_SIZE> m_object_pool;
    
    std::unique_ptr<AudioBlock> m_current_block;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H