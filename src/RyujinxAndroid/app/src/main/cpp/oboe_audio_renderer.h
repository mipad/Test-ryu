// oboe_audio_renderer.h (修复版本)
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
        int32_t underrun_count = 0;
        int32_t stream_restart_count = 0;
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    class HighPerformanceAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit HighPerformanceAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    class HighPerformanceErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit HighPerformanceErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    // 高性能环形缓冲区 - 无锁设计
    class LockFreeRingBuffer {
    public:
        explicit LockFreeRingBuffer(size_t capacity, int32_t channels);
        ~LockFreeRingBuffer();
        
        bool Write(const int16_t* data, size_t frames);
        size_t Read(int16_t* output, size_t frames);
        size_t Available() const;
        void Clear();
        
        size_t GetCapacity() const { return m_capacity; }
        size_t GetBufferSize() const { return m_buffer.size(); }
        
    private:
        size_t m_samples_capacity;  // 先声明 samples_capacity
        std::vector<int16_t> m_buffer;
        std::atomic<size_t> m_read_pos{0};
        std::atomic<size_t> m_write_pos{0};
        size_t m_capacity;
        int32_t m_channels;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();

    // 高性能配置 - 修改为返回void，直接修改传入的builder
    void ConfigureForPerformance(oboe::AudioStreamBuilder& builder);

    // 回调处理函数
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<LockFreeRingBuffer> m_ring_buffer;
    std::unique_ptr<HighPerformanceAudioCallback> m_audio_callback;
    std::unique_ptr<HighPerformanceErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<float> m_volume{1.0f};
    
    // 设备声道数
    int32_t device_channels = 2;
    
    // 性能统计
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_stream_restart_count{0};
    
    // 性能优化参数 - 基于yuzu配置
    static constexpr size_t BUFFER_DURATION_MS = 50;
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240; // 与yuzu相同
    static constexpr int32_t TARGET_SAMPLE_RATE = 48000; // 与yuzu相同
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H