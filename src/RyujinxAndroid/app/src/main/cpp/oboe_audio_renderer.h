// oboe_audio_renderer.h (简化版本)
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>
#include <queue>
#include <list>

namespace RyujinxOboe {

class OboeAudioRenderer {
public:
    static OboeAudioRenderer& GetInstance();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    void Shutdown();
    
    bool WriteAudio(const int16_t* data, int32_t num_frames);
    
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { return m_stream && m_stream->getState() == oboe::StreamState::Started; }
    int32_t GetBufferedFrames() const;
    
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }

    void Reset();

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    struct SampleBuffer {
        std::vector<int16_t> samples;
        size_t sample_count = 0;
        size_t samples_played = 0;
        bool consumed = true;
    };

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

    class SampleBufferQueue {
    public:
        explicit SampleBufferQueue(size_t max_buffers = 32) : m_max_buffers(max_buffers) {}
        
        bool Write(const int16_t* samples, size_t sample_count);
        size_t Read(int16_t* output, size_t samples_requested);
        size_t Available() const;
        void Clear();
        
    private:
        std::queue<SampleBuffer> m_buffers;
        SampleBuffer m_playing_buffer;
        size_t m_max_buffers;
        mutable std::mutex m_mutex;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<SampleBufferQueue> m_sample_queue;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    
    // 基本统计（用于调试）
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int32_t> m_underrun_count{0};
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
    static constexpr int32_t TARGET_SAMPLE_RATE = 48000;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H