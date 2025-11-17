// oboe_audio_renderer.h
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>
#include <queue>

namespace RyujinxOboe {

// 采样格式定义
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

    // 稳定回调控制
    void SetStabilizedCallbackEnabled(bool enabled);
    bool IsStabilizedCallbackEnabled() const { return m_stabilized_callback_enabled.load(); }
    void SetStabilizedCallbackIntensity(float intensity);
    float GetStabilizedCallbackIntensity() const { return m_stabilized_callback_intensity.load(); }

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    // 原始格式缓冲区结构
    struct RawSampleBuffer {
        std::vector<uint8_t> data;
        size_t data_size = 0;
        size_t data_played = 0;
        int32_t sample_format = 1;
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
        
        bool onError(oboe::AudioStream* audioStream, oboe::Result error) override;
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
        int32_t m_current_format = 1;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamError(oboe::AudioStream* audioStream, oboe::Result error);
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
    
    // 前向声明稳定回调类
    class StabilizedAudioCallback;
    std::shared_ptr<StabilizedAudioCallback> m_stabilized_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_stabilized_callback_enabled{true};
    std::atomic<float> m_stabilized_callback_intensity{0.3f};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{1};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // 性能统计
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_stream_restart_count{0};
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
