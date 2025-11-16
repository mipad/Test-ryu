// oboe_audio_renderer.h (完整格式支持)
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
#include <unordered_map>

namespace RyujinxOboe {

// 采样格式定义 (与C#层 SampleFormat.cs 完全一致)
enum SampleFormat {
    INVALID = 0,
    PCM_INT8 = 1,
    PCM_INT16 = 2,
    PCM_INT24 = 3,
    PCM_INT32 = 4,
    PCM_FLOAT = 5,
    ADPCM = 6
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
        std::string audio_api = "Unknown";
        std::string sharing_mode = "Unknown";
        std::string sample_format = "Unknown";
        int32_t sample_rate = 0;
        int32_t frames_per_burst = 0;
        int32_t buffer_size = 0;
        size_t buffer_memory_usage = 0;
        double average_latency_ms = 0.0;
        int32_t format_conversion_count = 0;
        int32_t adpcm_decoded_count = 0;
    };
    
    PerformanceStats GetStats() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    // 增强的原始格式缓冲区结构
    struct RawSampleBuffer {
        std::vector<uint8_t> data;
        size_t data_size = 0;
        size_t data_played = 0;
        int32_t sample_format = 2; // PCM16 by default (对应C#的PcmInt16=2)
        bool consumed = true;
        uint64_t session_id = 0;
    };

    // ADPCM 解码状态
    struct ADPCMState {
        int16_t predictor = 0;
        int8_t step_index = 0;
        int16_t step = 0;
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

    // 增强的原始格式缓冲区队列
    class RawSampleBufferQueue {
    public:
        explicit RawSampleBufferQueue(size_t max_buffers = 64) : 
            m_max_buffers(max_buffers), 
            m_max_total_size(4 * 1024 * 1024) {} // 4MB 总大小限制
        
        bool WriteRaw(const uint8_t* data, size_t data_size, int32_t sample_format, uint64_t session_id = 0);
        size_t ReadRaw(uint8_t* output, size_t output_size, int32_t target_format, int32_t target_channels);
        size_t Available() const;
        void Clear();
        size_t GetMemoryUsage() const;
        bool IsEmpty() const;
        
        int32_t GetCurrentFormat() const { return m_current_format; }
        
    private:
        std::queue<RawSampleBuffer> m_buffers;
        RawSampleBuffer m_playing_buffer;
        size_t m_max_buffers;
        size_t m_max_total_size;
        size_t m_current_total_size = 0;
        mutable std::mutex m_mutex;
        int32_t m_current_format = 2; // PCM16 by default
    };

    // 完整的格式转换函数
    static bool ConvertFormat(const uint8_t* input, uint8_t* output, size_t frames, 
                             int32_t input_format, int32_t output_format, int32_t input_channels, int32_t output_channels);
    
    // 特定格式转换函数
    static bool ConvertPCM8ToPCM16(const uint8_t* input, int16_t* output, size_t samples);
    static bool ConvertPCM16ToPCM8(const int16_t* input, uint8_t* output, size_t samples);
    static bool ConvertPCM24ToPCM32(const uint8_t* input, int32_t* output, size_t samples);
    static bool ConvertPCM32ToPCM24(const int32_t* input, uint8_t* output, size_t samples);
    static bool ConvertPCM16ToFloat(const int16_t* input, float* output, size_t samples);
    static bool ConvertFloatToPCM16(const float* input, int16_t* output, size_t samples);
    static bool ConvertPCM32ToFloat(const int32_t* input, float* output, size_t samples);
    static bool ConvertFloatToPCM32(const float* input, int32_t* output, size_t samples);
    
    // ADPCM 解码函数 (GC-ADPCM/Nintendo ADPCM)
    static bool DecodeADPCM(const uint8_t* input, size_t input_size, int16_t* output, size_t output_samples, 
                           ADPCMState& state, int32_t channels);
    static int16_t DecodeADPCMSample(uint8_t nibble, ADPCMState& state);
    
    // 声道转换函数
    static bool ConvertChannels(const uint8_t* input, uint8_t* output, size_t frames, 
                               int32_t input_format, int32_t input_channels, int32_t output_channels);
    
    // 音量应用函数
    static void ApplyVolume(void* data, size_t frames, int32_t format, int32_t channels, float volume);

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    // 格式映射函数
    oboe::AudioFormat MapSampleFormat(int32_t format);
    const char* GetFormatName(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    static bool IsFormatSupported(int32_t format);
    
    // 缓冲区优化
    bool OptimizeBufferSize();

    // 重采样支持
    bool NeedsResampling() const;
    bool ResampleAudio(const uint8_t* input, size_t input_frames, uint8_t* output, size_t output_frames, int32_t format);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<RawSampleBufferQueue> m_raw_sample_queue;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{2}; // PCM16 by default (对应C#的PcmInt16=2)
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // 性能统计增强
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    std::atomic<int32_t> m_underrun_count{0};
    std::atomic<int32_t> m_stream_restart_count{0};
    std::atomic<int32_t> m_frames_per_burst{0};
    std::atomic<int32_t> m_buffer_size{0};
    std::atomic<size_t> m_buffer_memory_usage{0};
    std::atomic<int32_t> m_format_conversion_count{0};
    std::atomic<int32_t> m_adpcm_decoded_count{0};
    
    std::string m_current_audio_api = "Unknown";
    std::string m_current_sharing_mode = "Unknown";
    std::string m_current_sample_format = "PCM16";
    
    // ADPCM 解码状态
    ADPCMState m_adpcm_state;
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
    
    // 重采样相关
    int32_t m_device_sample_rate = 48000;
    bool m_resampling_enabled = false;
    
    // 格式支持标志
    static constexpr bool SUPPORT_PCM8 = true;
    static constexpr bool SUPPORT_PCM16 = true;
    static constexpr bool SUPPORT_PCM24 = true;
    static constexpr bool SUPPORT_PCM32 = true;
    static constexpr bool SUPPORT_FLOAT = true;
    static constexpr bool SUPPORT_ADPCM = true;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H