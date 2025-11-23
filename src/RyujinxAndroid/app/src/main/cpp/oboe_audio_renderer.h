#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <thread>
#include <chrono>
#include <condition_variable>
#include "LockFreeQueue.h"

namespace RyujinxOboe {

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

struct AudioBlock {
    static constexpr size_t BLOCK_SIZE = 1024; // 减小块大小以减少延迟
    
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

    void Reset();
    void PreallocateBlocks(size_t count);
    
    // 新增：获取实际延迟
    int64_t GetCurrentOutputLatency() const;

private:
    class LowLatencyCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit LowLatencyCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    class LowLatencyErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit LowLatencyErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    private:
        OboeAudioRenderer* m_renderer;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForLowLatency(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    oboe::AudioFormat MapSampleFormat(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    bool OptimizeBufferSizeForLowLatency();
    bool TryOpenStreamWithRetry(int maxRetryCount = 3);
    
    // 新增：直接写入音频数据，不经过队列
    bool WriteAudioDirect(const void* data, int32_t num_frames, int32_t sampleFormat);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<LowLatencyCallback> m_audio_callback;
    std::unique_ptr<LowLatencyErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::mutex m_data_mutex;
    std::condition_variable m_data_condition;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_need_data{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // 直接缓冲区，减少队列延迟
    std::vector<uint8_t> m_direct_buffer;
    size_t m_direct_buffer_pos = 0;
    bool m_use_direct_buffer = true;
    
    static constexpr uint32_t OBJECT_POOL_SIZE = 32; // 减少池大小
    
    LockFreeObjectPool<AudioBlock, OBJECT_POOL_SIZE> m_object_pool;
    SegmentBasedLockFreeQueue<std::unique_ptr<AudioBlock>> m_audio_queue;
    
    std::unique_ptr<AudioBlock> m_current_block;
    
    // 延迟统计
    std::atomic<int64_t> m_total_frames_written{0};
    std::atomic<int64_t> m_total_frames_played{0};
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H