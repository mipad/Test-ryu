// oboe_audio_renderer.h (完整实现)
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
#include <functional>

namespace RyujinxOboe {

// 采样格式定义 (与C#层保持一致)
enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

class OboeAudioRenderer {
public:
    static OboeAudioRenderer& GetInstance();

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

    struct PerformanceStats {
        int64_t frames_written = 0;
        int64_t frames_played = 0;
        int32_t underrun_count = 0;
        int32_t stream_restart_count = 0;
        std::string audio_api = "Unknown";
        std::string sharing_mode = "Unknown";
        std::string sample_format = "Unknown";
        int32_t frames_per_burst = 0;
        int32_t buffer_size = 0;
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    // 原始格式缓冲区结构
    struct RawSampleBuffer {
        std::vector<uint8_t> data;
        size_t data_size = 0;
        size_t data_played = 0;
        int32_t sample_format = 1; // PCM16 by default
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

    // 原始格式缓冲区队列
    class RawSampleBufferQueue {
    public:
        explicit RawSampleBufferQueue(size_t max_buffers = 32) : m_max_buffers(max_buffers) {}
        
        bool WriteRaw(const uint8_t* data, size_t data_size, int32_t sample_format);
        size_t ReadRaw(uint8_t* output, size_t output_size, int32_t target_format);
        size_t Available() const;
        void Clear();
        
        int32_t GetCurrentFormat() const { return m_current_format; }
        
    private:
        std::queue<RawSampleBuffer> m_buffers;
        RawSampleBuffer m_playing_buffer;
        size_t m_max_buffers;
        mutable std::mutex m_mutex;
        int32_t m_current_format = 1; // PCM16 by default
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    // 格式转换函数
    oboe::AudioFormat MapSampleFormat(int32_t format);
    const char* GetFormatName(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    
    // 缓冲区优化
    bool OptimizeBufferSize();

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<RawSampleBufferQueue> m_raw_sample_queue;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{1}; // PCM16 by default
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // 性能统计
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_stream_restart_count{0};
    std::atomic<int32_t> m_frames_per_burst{0};
    std::atomic<int32_t> m_buffer_size{0};
    
    std::string m_current_audio_api = "Unknown";
    std::string m_current_sharing_mode = "Unknown";
    std::string m_current_sample_format = "PCM16";
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
    static constexpr int32_t TARGET_SAMPLE_RATE = 48000;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H