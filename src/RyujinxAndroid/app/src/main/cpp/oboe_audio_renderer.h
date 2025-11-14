// oboe_audio_renderer.h (完整优化版本)
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

namespace RyujinxOboe {

// 采样格式枚举
enum class SampleFormat {
    PCM8,
    PCM16,
    PCM24,
    PCM32,
    PCMFloat
};

// 音频格式转换类
class AudioFormatConverter {
public:
    static bool ConvertToPCM16(const uint8_t* input, int16_t* output,
                              size_t sample_count, SampleFormat format);
    
    static void ApplyVolume(int16_t* samples, size_t sample_count, float volume);
    
    // 支持从各种格式转换到PCM16
    static void ConvertPCM8ToPCM16(const uint8_t* input, int16_t* output, size_t sample_count);
    static void ConvertPCM24ToPCM16(const uint8_t* input, int16_t* output, size_t sample_count);
    static void ConvertPCM32ToPCM16(const int32_t* input, int16_t* output, size_t sample_count);
    static void ConvertFloatToPCM16(const float* input, int16_t* output, size_t sample_count);
};

// 声道下混类
class ChannelDownmixer {
public:
    // 6声道到2声道下混
    static void Downmix51ToStereo(const int16_t* input, int16_t* output,
                                 size_t frame_count, const float* coefficients = nullptr);
    
    // 立体声到单声道下混
    static void DownmixStereoToMono(const int16_t* input, int16_t* output,
                                   size_t frame_count);
    
    // 通用的声道重映射
    static void RemapChannels(const int16_t* input, int16_t* output,
                             size_t frame_count, int input_channels,
                             int output_channels, const int* channel_map);
};

// 高性能环形缓冲区
class HighPerformanceRingBuffer {
private:
    std::vector<int16_t> buffer_;
    size_t head_ = 0;
    size_t tail_ = 0;
    size_t size_ = 0;
    size_t capacity_;
    mutable std::mutex mutex_;
    
public:
    explicit HighPerformanceRingBuffer(size_t capacity);
    size_t WriteBulk(const int16_t* data, size_t sample_count);
    size_t ReadBulk(int16_t* output, size_t samples_requested);
    size_t Available() const;
    void Clear();
};

// 音频重采样类
class AudioResampler {
public:
    // 简单的线性重采样
    static void ResampleLinear(const int16_t* input, int16_t* output,
                              size_t input_frames, size_t output_frames,
                              int channels, double ratio);
    
    // 高质量的重采样
    static void ResampleHighQuality(const int16_t* input, int16_t* output,
                                   size_t input_frames, size_t output_frames,
                                   int channels, double ratio);
};

class OboeAudioRenderer {
public:
    static OboeAudioRenderer& GetInstance();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    void Shutdown();
    
    // 基本音频写入（PCM16格式）
    bool WriteAudio(const int16_t* data, int32_t num_frames);
    
    // 高级音频写入（支持格式转换和声道下混）
    bool WriteAudioConverted(const uint8_t* data, int32_t num_frames, 
                            SampleFormat format, int32_t input_channels);
    
    // 带声道下混的音频写入
    bool WriteAudioWithDownmix(const int16_t* data, int32_t num_frames,
                              int32_t input_channels, int32_t output_channels);
    
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { return m_stream && m_stream->getState() == oboe::StreamState::Started; }
    int32_t GetBufferedFrames() const;
    
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }

    void Reset();

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    struct SampleBuffer {
        std::vector<int16_t> samples;
        size_t sample_count = 0;
        size_t samples_played = 0;
        bool consumed = true;
    };

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

    class SampleBufferQueue {
    public:
        explicit SampleBufferQueue(size_t max_buffers = 32) : m_max_buffers(max_buffers) {}
        
        bool Write(const int16_t* samples, size_t sample_count);
        size_t Read(int16_t* output, size_t samples_requested);
        size_t Available() const;
        void Clear();
        
    private:
        std::queue<SampleBuffer> m_buffers;
        SampleBuffer m_playing_buffer;
        size_t m_max_buffers;
        mutable std::mutex m_mutex;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<SampleBufferQueue> m_sample_queue;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    
    // 基本统计（用于调试）
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int32_t> m_underrun_count{0};
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
    static constexpr int32_t TARGET_SAMPLE_RATE = 48000;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H