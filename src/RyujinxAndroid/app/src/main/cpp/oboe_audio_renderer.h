#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <cmath>
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

class DspProcessor {
public:
    DspProcessor() = default;
    
    void SetVolume(float volume) {
        m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
    }
    
    float GetVolume() const {
        return m_volume.load();
    }
    
    // 处理音频数据，应用DSP效果
    void ProcessAudio(void* data, size_t size, int32_t sample_format, int32_t channels);
    
    // 批量处理音频数据
    void ProcessAudioBatch(void* data, size_t frame_count, int32_t sample_format, int32_t channels);
    
private:
    std::atomic<float> m_volume{1.0f};
    
    // 不同格式的音量应用
    void ApplyVolumeInt16(int16_t* samples, size_t sample_count, float volume);
    void ApplyVolumeInt32(int32_t* samples, size_t sample_count, float volume);
    void ApplyVolumeFloat(float* samples, size_t sample_count, float volume);
    
    // 处理24位PCM (存储为32位，高8位为0)
    void ApplyVolumeInt24(int32_t* samples, size_t sample_count, float volume);
    
    // 安全的样本转换
    int32_t ScaleSampleInt16(int16_t sample, float volume);
    int32_t ScaleSampleInt32(int32_t sample, float volume);
    float ScaleSampleFloat(float sample, float volume);
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
    float GetVolume() const { return m_dsp_processor.GetVolume(); }

    void Reset();

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

    oboe::AudioFormat MapSampleFormat(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    bool OptimizeBufferSize();

    // DSP处理
    void ApplyDspEffects(void* audio_data, size_t data_size, int32_t sample_format);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 256;
    static constexpr uint32_t OBJECT_POOL_SIZE = 512;
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    LockFreeObjectPool<AudioBlock, OBJECT_POOL_SIZE> m_object_pool;
    
    std::unique_ptr<AudioBlock> m_current_block;
    
    // DSP处理器
    DspProcessor m_dsp_processor;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H