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

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

struct AudioBlock {
    static constexpr size_t BLOCK_SIZE = 4096;
    
    uint8_t data[BLOCK_SIZE];
    size_t data_size = 0;
    size_t data_played = 0;
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
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    bool InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat);
    void Shutdown();
    
    bool WriteAudio(const int16_t* data, int32_t num_frames);
    bool WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat);
    
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { return m_stream_started.load(); }
    int32_t GetBufferedFrames() const;
    
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }

    void Reset();

private:
    class SimpleAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit SimpleAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, 
                                              void* audioData, 
                                              int32_t num_frames) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    class SimpleErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit SimpleErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    // 内部方法
    bool ConfigureAndOpenStream();
    
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, 
                                          void* audioData, 
                                          int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);
    
    // 缓冲区管理
    void ClearAllBuffers();
    bool TryRestartStream();
    
    // 辅助方法
    static oboe::AudioFormat MapSampleFormat(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    
    // 对象池管理
    std::unique_ptr<AudioBlock> AcquireBlock();
    void ReleaseBlock(std::unique_ptr<AudioBlock> block);
    void InitializePool(size_t size);
    
    // 成员变量
    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<SimpleAudioCallback> m_audio_callback;
    std::unique_ptr<SimpleErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::mutex m_pool_mutex;
    
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_needs_restart{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    
    // 音频队列
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 64; // 更小的队列，降低延迟
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    
    std::unique_ptr<AudioBlock> m_current_block;
    std::vector<std::unique_ptr<AudioBlock>> m_block_pool;
    
    // 性能统计
    std::chrono::steady_clock::time_point m_last_write_time;
    std::atomic<int32_t> m_consecutive_underruns{0};
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H