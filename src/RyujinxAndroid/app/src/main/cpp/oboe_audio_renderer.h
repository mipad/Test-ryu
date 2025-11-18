// oboe_audio_renderer.h (使用LockFreeQueue)
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
#include "LockFreeQueue.h"

namespace RyujinxOboe {

// 采样格式定义 (与C#层保持一致)
enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

// 音频块结构 - 固定大小避免动态分配
struct AudioBlock {
    static constexpr size_t BLOCK_SIZE = 4096; // 4KB per block
    
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
        return data_size - data_played;
    }
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
        int32_t pool_hit_count = 0;
        int32_t pool_miss_count = 0;
        std::string audio_api = "Unknown";
        std::string sharing_mode = "Unknown";
        std::string sample_format = "Unknown";
        int32_t sample_rate = 0;
        int32_t frames_per_burst = 0;
        int32_t buffer_size = 0;
        int32_t queue_size = 0;
        int32_t pool_available = 0;
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

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
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // Lock-free 音频队列和对象池
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 256;  // 256个音频块
    static constexpr uint32_t OBJECT_POOL_SIZE = 512;  // 512个对象池容量
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    LockFreeObjectPool<AudioBlock, OBJECT_POOL_SIZE> m_object_pool;
    
    // 当前播放块
    std::unique_ptr<AudioBlock> m_current_block;
    
    // 性能统计
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_stream_restart_count{0};
    std::atomic<int32_t> m_pool_hit_count{0};
    std::atomic<int32_t> m_pool_miss_count{0};
    std::atomic<int32_t> m_frames_per_burst{0};
    std::atomic<int32_t> m_buffer_size{0};
    
    std::string m_current_audio_api = "Unknown";
    std::string m_current_sharing_mode = "Unknown";
    std::string m_current_sample_format = "PCM16";
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H