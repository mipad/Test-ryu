// oboe_audio_renderer.h (彻底解决耳鸣版本)
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

namespace RyujinxOboe {

class OboeAudioRenderer {
public:
    static OboeAudioRenderer& GetInstance();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    void Shutdown();
    
    // 直接写入PCM16数据，避免格式转换问题
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

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    class SimpleAudioCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit SimpleAudioCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

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

    // 简单的环形缓冲区，避免复杂的锁机制
    class SimpleRingBuffer {
    public:
        explicit SimpleRingBuffer(size_t capacity);
        ~SimpleRingBuffer();
        
        bool Write(const int16_t* data, size_t frames, int32_t channels);
        size_t Read(int16_t* output, size_t frames, int32_t channels);
        size_t Available() const;
        void Clear();
        
    private:
        std::vector<int16_t> m_buffer;
        std::atomic<size_t> m_read_pos{0};
        std::atomic<size_t> m_write_pos{0};
        size_t m_capacity;
        int32_t m_channels{2};
    };

    bool OpenStream();
    void CloseStream();

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<SimpleRingBuffer> m_ring_buffer;
    std::unique_ptr<SimpleAudioCallback> m_audio_callback;
    std::unique_ptr<SimpleErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<float> m_volume{1.0f};
    
    std::atomic<int64_t> m_frames_written{0};
    std::atomic<int64_t> m_frames_played{0};
    
    static constexpr size_t BUFFER_DURATION_MS = 100; // 100ms缓冲区
    static constexpr int32_t TARGET_FRAMES_PER_CALLBACK = 256;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H