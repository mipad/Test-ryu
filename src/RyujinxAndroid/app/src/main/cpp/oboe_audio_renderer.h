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
    int32_t sample_format = PCM_INT16;
    bool consumed = true;
    
    void clear() {
        // 清空整个数据缓冲区，避免残留的旧数据导致杂音
        std::memset(data, 0, BLOCK_SIZE);
        data_size = 0;
        data_played = 0;
        sample_format = PCM_INT16;
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

    // 添加效果器相关函数
    bool SetBiquadFilterParameters(const uint8_t* param_data);
    void EnableBiquadFilter(bool enable);
    void ClearBiquadFilters();

private:
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

    // Biquad 滤波器状态结构
    struct BiquadFilterState {
        float b0 = 1.0f;      // 分子系数 b0
        float b1 = 0.0f;      // 分子系数 b1
        float b2 = 0.0f;      // 分子系数 b2
        float a1 = 0.0f;      // 分母系数 a1
        float a2 = 0.0f;      // 分母系数 a2
        
        // 滤波器历史状态（支持最多6声道）
        float x1[6] = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};  // 输入历史 n-1
        float x2[6] = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};  // 输入历史 n-2
        float y1[6] = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};  // 输出历史 n-1
        float y2[6] = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};  // 输出历史 n-2
        
        bool enabled = false;
        int32_t channelCount = 2;
        
        void reset() {
            for (int i = 0; i < 6; i++) {
                x1[i] = 0.0f;
                x2[i] = 0.0f;
                y1[i] = 0.0f;
                y2[i] = 0.0f;
            }
        }
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

    // 效果器处理函数
    void ApplyBiquadFilterInt16(int16_t* audio_data, int32_t num_frames, int32_t channels);
    void ApplyBiquadFilterFloat(float* audio_data, int32_t num_frames, int32_t channels);
    void ApplyBiquadFilterInt32(int32_t* audio_data, int32_t num_frames, int32_t channels);
    void ApplyVolumeInt16(int16_t* audio_data, int32_t num_frames, int32_t channels, float volume);
    void ApplyVolumeFloat(float* audio_data, int32_t num_frames, int32_t channels, float volume);
    void ApplyVolumeInt32(int32_t* audio_data, int32_t num_frames, int32_t channels, float volume);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::mutex m_filter_mutex;  // 用于保护效果器状态
    
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    
    // 添加原子计数器来准确跟踪缓冲帧数
    std::atomic<int64_t> m_buffered_frames{0};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 256;
    static constexpr uint32_t OBJECT_POOL_SIZE = 512;
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    LockFreeObjectPool<AudioBlock, OBJECT_POOL_SIZE> m_object_pool;
    
    std::unique_ptr<AudioBlock> m_current_block;
    
    // 效果器状态
    std::vector<BiquadFilterState> m_biquad_filters;
    bool m_biquad_enabled = false;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H