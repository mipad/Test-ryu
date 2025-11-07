// oboe_audio_renderer.h (实时音频版本)
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>
#include <list>
#include <queue>
#include <functional>
#include <android/log.h>

#define LOG_TAG "RyujinxOboe"
#define LOGI(...) // 完全禁用信息日志
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGW(...) // 禁用警告日志
#define LOGD(...) // 禁用调试日志

namespace RyujinxOboe {

class OboeAudioRenderer {
public:
    static OboeAudioRenderer& GetInstance();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    void Shutdown();
    
    // 直接写入PCM16数据
    bool WriteAudio(const int16_t* data, int32_t num_frames);
    
    // 状态查询
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { return m_stream && m_stream->getState() == oboe::StreamState::Started; }
    int32_t GetBufferedFrames() const;
    
    // 音量控制
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }

    // 重置音频流
    void Reset();

    // 实时音频优化
    void SetRealTimeMode(bool enabled);
    bool IsRealTimeMode() const { return m_real_time_mode.load(); }

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    class RealTimeAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit RealTimeAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    class RealTimeErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit RealTimeErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    // 实时音频缓冲区 - 使用队列确保数据顺序
    class RealTimeAudioBuffer {
    public:
        RealTimeAudioBuffer(size_t frame_capacity, int32_t channels);
        ~RealTimeAudioBuffer();
        
        bool Write(const int16_t* data, size_t frames);
        size_t Read(int16_t* output, size_t frames);
        size_t Available() const;
        void Clear();
        size_t GetFreeSpace() const;
        
        // 实时音频优化
        void SetLowLatencyMode(bool enabled);
        void AdjustBufferBasedOnUsage();
        
    private:
        struct AudioChunk {
            std::vector<int16_t> data;
            size_t frames;
        };
        
        std::queue<AudioChunk> m_chunks;
        mutable std::mutex m_queue_mutex;
        size_t m_total_frames;
        size_t m_frame_capacity;
        int32_t m_channels;
        bool m_low_latency_mode;
        
        // 使用统计
        size_t m_frames_written;
        size_t m_frames_read;
        double m_usage_ratio;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();

    // 实时音频配置
    void ConfigureForRealTimeAudio(oboe::AudioStreamBuilder& builder);

    // 回调处理函数
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamError(oboe::Result error);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<RealTimeAudioBuffer> m_audio_buffer;
    std::unique_ptr<RealTimeAudioCallback> m_audio_callback;
    std::unique_ptr<RealTimeErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_real_time_mode{true}; // 默认启用实时模式
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<float> m_volume{1.0f};
    
    // 实时音频统计
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_overrun_count{0};
    
    // 实时音频优化参数
    static constexpr size_t MIN_BUFFER_DURATION_MS = 80;   // 最小缓冲区
    static constexpr size_t MAX_BUFFER_DURATION_MS = 200;  // 最大缓冲区
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK = 256;
    
    // 始终使用AAudio，最低延迟
    static constexpr oboe::PerformanceMode PERFORMANCE_MODE = oboe::PerformanceMode::LowLatency;
    static constexpr oboe::SharingMode SHARING_MODE = oboe::SharingMode::Exclusive;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H