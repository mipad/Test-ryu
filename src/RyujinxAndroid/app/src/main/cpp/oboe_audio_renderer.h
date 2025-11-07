// oboe_audio_renderer.h (声道修复版本)
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
    bool WriteAudio(const int16_t* data, int32_t num_frames, int32_t input_channels);
    
    // 状态查询
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { return m_stream && m_stream->getState() == oboe::StreamState::Started; }
    int32_t GetBufferedFrames() const;
    
    // 音量控制
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }

    // 重置音频流
    void Reset();

    // 声道信息
    int32_t GetCurrentChannelCount() const { return m_channel_count.load(); }

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    class ChannelAwareAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit ChannelAwareAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    class ChannelAwareErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit ChannelAwareErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
        void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    // 声道感知的环形缓冲区
    class ChannelAwareRingBuffer {
    public:
        explicit ChannelAwareRingBuffer(size_t capacity, int32_t channels);
        ~ChannelAwareRingBuffer();
        
        bool Write(const int16_t* data, size_t frames, int32_t input_channels);
        size_t Read(int16_t* output, size_t frames);
        size_t Available() const;
        void Clear();
        size_t GetFreeSpace() const;
        
        size_t GetCapacity() const { return m_capacity; }
        int32_t GetChannels() const { return m_channels; }
        
    private:
        size_t m_samples_capacity;
        std::vector<int16_t> m_buffer;
        std::atomic<size_t> m_read_pos{0};
        std::atomic<size_t> m_write_pos{0};
        size_t m_capacity;
        int32_t m_channels;
        
        // 声道转换辅助函数
        bool ConvertAndWrite(const int16_t* data, size_t frames, int32_t input_channels);
        bool Convert6To2(const int16_t* data, size_t frames, std::vector<int16_t>& output);
        bool Convert2To6(const int16_t* data, size_t frames, std::vector<int16_t>& output);
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();

    // 声道感知配置
    void ConfigureForChannels(oboe::AudioStreamBuilder& builder);

    // 回调处理函数
    oboe::DataCallbackResult OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamError(oboe::Result error);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<ChannelAwareRingBuffer> m_ring_buffer;
    std::unique_ptr<ChannelAwareAudioCallback> m_audio_callback;
    std::unique_ptr<ChannelAwareErrorCallback> m_error_callback;
    
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
    
    // 声道处理参数
    static constexpr size_t BUFFER_DURATION_MS = 100; // 平衡延迟和稳定性
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK = 256;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H