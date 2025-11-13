// oboe_audio_renderer.h (AAudio + 独占模式)
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
#include <android/log.h>

// 移除 LOG_TAG 定义，避免与 ryujinx.cpp 冲突
// 在每个 cpp 文件中单独定义 LOG_TAG

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

    struct PerformanceStats {
        int64_t frames_written = 0;
        int64_t frames_played = 0;
        int32_t underrun_count = 0;
        int32_t stream_restart_count = 0;
        std::string audio_api = "Unknown";
        std::string sharing_mode = "Unknown";
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    // 基于yuzu的样本缓冲区结构
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

    // 基于yuzu的样本缓冲区队列
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
    
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_stream_restart_count{0};
    
    // 性能统计
    std::string m_current_audio_api = "Unknown";
    std::string m_current_sharing_mode = "Unknown";
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
    static constexpr int32_t TARGET_SAMPLE_RATE = 48000;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H