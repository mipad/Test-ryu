#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <vector>
#include "LockFreeQueue.h"

namespace RyujinxOboe {

// ========== 音频调试宏 ==========
#ifdef ANDROID
#include <android/log.h>
#define OBOE_LOG_TAG "RyujinxOboe"
#define OBOE_LOGV(...) __android_log_print(ANDROID_LOG_VERBOSE, OBOE_LOG_TAG, __VA_ARGS__)
#define OBOE_LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, OBOE_LOG_TAG, __VA_ARGS__)
#define OBOE_LOGI(...) __android_log_print(ANDROID_LOG_INFO, OBOE_LOG_TAG, __VA_ARGS__)
#define OBOE_LOGW(...) __android_log_print(ANDROID_LOG_WARN, OBOE_LOG_TAG, __VA_ARGS__)
#define OBOE_LOGE(...) __android_log_print(ANDROID_LOG_ERROR, OBOE_LOG_TAG, __VA_ARGS__)
#else
#define OBOE_LOGV(...) 
#define OBOE_LOGD(...)
#define OBOE_LOGI(...)
#define OBOE_LOGW(...)
#define OBOE_LOGE(...)
#endif

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

struct AudioBlock {
    static constexpr size_t BLOCK_SIZE = 8192;  // 增加块大小，减少碎片化
    
    uint8_t data[BLOCK_SIZE];
    size_t data_size = 0;
    size_t data_played = 0;
    int32_t sample_format = PCM_INT16;
    bool consumed = true;
    
    void clear() {
        data_size = 0;
        data_played = 0;
        consumed = true;
    }
    
    size_t available() const {
        return data_size > data_played ? data_size - data_played : 0;
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
    
    float GetLatencyMs() const;
    int32_t GetUnderrunCount() const { return m_underrun_count.load(); }
    int32_t GetOverrunCount() const { return m_overrun_count.load(); }
    
    void Reset();
    bool RestartStream();

private:
    class AudioStreamCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit AudioStreamCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureStreamBuilder(oboe::AudioStreamBuilder& builder);
    
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);
    
    void ProcessAudioData(void* audioData, int32_t num_frames);
    void ApplyVolume(void* audioData, int32_t num_frames, int32_t format);
    
    oboe::AudioFormat MapSampleFormat(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    
    bool OptimizeBufferSize();
    bool TryRecoverStream();
    void ClearAllBuffers();
    
    std::unique_ptr<AudioBlock> AcquireBlock();
    void ReleaseBlock(std::unique_ptr<AudioBlock> block);
    void InitializePool(size_t pool_size = 128);
    
    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AudioStreamCallback> m_callback;
    
    std::mutex m_stream_mutex;
    std::mutex m_pool_mutex;
    
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_active{false};
    std::atomic<bool> m_recovery_pending{false};
    std::atomic<int32_t> m_error_count{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_overrun_count{0};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    std::atomic<float> m_target_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // 音频队列和对象池
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 512;
    static constexpr uint32_t MAX_POOL_SIZE = 256;
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    std::vector<std::unique_ptr<AudioBlock>> m_block_pool;
    
    std::unique_ptr<AudioBlock> m_current_block;
    
    // 用于音量平滑过渡
    float m_current_volume = 1.0f;
    static constexpr float VOLUME_RAMP_SPEED = 0.05f;
    
    // 性能监控
    std::chrono::steady_clock::time_point m_last_buffer_time;
    std::atomic<int32_t> m_total_written_frames{0};
    std::atomic<int32_t> m_total_played_frames{0};
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H