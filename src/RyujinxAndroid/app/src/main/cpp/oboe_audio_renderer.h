// oboe_audio_renderer.h (音频拉伸版本)
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
#include <algorithm>
#include <android/log.h>

#define LOG_TAG "RyujinxOboe"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
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

    // 性能统计
    struct PerformanceStats {
        int64_t frames_written = 0;
        int64_t frames_played = 0;
        int32_t write_failures = 0;
        int32_t stream_restart_count = 0;
        int32_t buffer_overflows = 0;
        int32_t stretch_activations = 0;
        std::string mode = "Unknown";
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    class StretchAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit StretchAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    class StretchErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit StretchErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    // 音频拉伸缓冲区
    class AudioStretchBuffer {
    public:
        explicit AudioStretchBuffer(size_t capacity, int32_t channels);
        ~AudioStretchBuffer();
        
        bool Write(const int16_t* data, size_t frames);
        size_t Read(int16_t* output, size_t frames);
        size_t Available() const;
        void Clear();
        size_t GetFreeSpace() const;
        
        // 音频拉伸功能
        size_t ReadWithStretch(int16_t* output, size_t requested_frames, float stretch_factor);
        void SetStretchEnabled(bool enabled) { m_stretch_enabled = enabled; }
        bool IsStretchActive() const { return m_stretch_active; }
        
        size_t GetCapacity() const { return m_capacity; }
        
    private:
        // 简单的重叠相加拉伸算法
        size_t ApplyTimeStretch(int16_t* output, size_t requested_frames, float stretch_factor);
        
        std::vector<int16_t> m_buffer;
        std::atomic<size_t> m_read_pos{0};
        std::atomic<size_t> m_write_pos{0};
        size_t m_capacity;
        int32_t m_channels;
        size_t m_samples_capacity;
        
        // 拉伸相关变量
        bool m_stretch_enabled = false;
        bool m_stretch_active = false;
        std::vector<int16_t> m_stretch_history;
        size_t m_history_size = 512; // 历史缓冲区大小
        float m_current_stretch = 1.0f;
        size_t m_stretch_position = 0;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();

    // 优化配置 - 根据声道数选择不同模式
    void ConfigureForStretchMode(oboe::AudioStreamBuilder& builder);

    // 回调处理函数
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamError(oboe::Result error);

    // 音频拉伸控制
    float CalculateStretchFactor(size_t available_frames, size_t requested_frames);
    void UpdateStretchState();

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AudioStretchBuffer> m_stretch_buffer;
    std::unique_ptr<StretchAudioCallback> m_audio_callback;
    std::unique_ptr<StretchErrorCallback> m_error_callback;
    
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
    std::atomic<int32_t> m_stretch_activations{0};
    
    // 模式选择
    std::string m_current_mode;
    
    // 拉伸模式参数
    // 6声道: 音频拉伸模式 (150ms缓冲区，AAudio独占，智能拉伸)
    // 其他声道: 稳定模式 (200ms缓冲区，AAudio共享)
    static constexpr size_t BUFFER_DURATION_STRETCH_MS = 150;  // 6声道增加到150ms
    static constexpr size_t BUFFER_DURATION_STABLE_MS = 200;   // 其他声道
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK_STRETCH = 256;  // 6声道
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK_STABLE = 512;   // 其他声道
    
    // 拉伸阈值
    static constexpr size_t STRETCH_THRESHOLD = 64;  // 当可用帧数低于此值时启用拉伸
    static constexpr float MAX_STRETCH_FACTOR = 0.7f; // 最大拉伸系数 (降低播放速度)
    static constexpr float MIN_STRETCH_FACTOR = 1.0f; // 最小拉伸系数 (正常速度)
    
    // 始终使用AAudio
    static constexpr oboe::PerformanceMode PERFORMANCE_MODE = oboe::PerformanceMode::LowLatency;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H