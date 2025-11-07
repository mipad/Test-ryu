// oboe_audio_renderer.h (混合模式版本)
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>
#include <list>
#include <android/log.h>

#define LOG_TAG "RyujinxOboe"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

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

    // 性能统计
    struct PerformanceStats {
        int64_t frames_written = 0;
        int64_t frames_played = 0;
        int32_t write_failures = 0;
        int32_t stream_restart_count = 0;
        int32_t buffer_overflows = 0;
        std::string mode = "Unknown";
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    class HybridAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit HybridAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    class HybridErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit HybridErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    // 混合环形缓冲区
    class HybridRingBuffer {
    public:
        explicit HybridRingBuffer(size_t capacity, int32_t channels);
        ~HybridRingBuffer();
        
        bool Write(const int16_t* data, size_t frames);
        size_t Read(int16_t* output, size_t frames);
        size_t Available() const;
        void Clear();
        size_t GetFreeSpace() const;
        
        size_t GetCapacity() const { return m_capacity; }
        size_t GetBufferSize() const { return m_buffer.size(); }
        
    private:
        size_t m_samples_capacity;
        std::vector<int16_t> m_buffer;
        std::atomic<size_t> m_read_pos{0};
        std::atomic<size_t> m_write_pos{0};
        size_t m_capacity;
        int32_t m_channels;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();

    // 混合配置 - 根据声道数选择不同模式
    void ConfigureForHybridMode(oboe::AudioStreamBuilder& builder);

    // 回调处理函数
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamError(oboe::Result error);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<HybridRingBuffer> m_ring_buffer;
    std::unique_ptr<HybridAudioCallback> m_audio_callback;
    std::unique_ptr<HybridErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<float> m_volume{1.0f};
    
    // 性能统计
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_write_failures{0};
    std::atomic<int32_t> m_stream_restart_count{0};
    std::atomic<int32_t> m_buffer_overflows{0};
    
    // 模式选择
    std::string m_current_mode;
    
    // 混合模式参数
    // 6声道: 高性能模式 (100ms缓冲区，AAudio独占)
    // 其他声道: 稳定模式 (200ms缓冲区，AAudio共享)
    static constexpr size_t BUFFER_DURATION_HIGH_PERF_MS = 100;  // 6声道
    static constexpr size_t BUFFER_DURATION_STABLE_MS = 200;     // 其他声道
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK_HIGH_PERF = 256;  // 6声道
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK_STABLE = 512;     // 其他声道
    
    // 始终使用AAudio
    static constexpr oboe::PerformanceMode PERFORMANCE_MODE = oboe::PerformanceMode::LowLatency;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H