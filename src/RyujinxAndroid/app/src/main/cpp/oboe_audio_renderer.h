// oboe_audio_renderer.h (基于yuzu实现)
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>
#include <memory>
#include <cstdint>
#include <list>

namespace RyujinxOboe {

class OboeSinkStream : public oboe::AudioStreamDataCallback,
                       public oboe::AudioStreamErrorCallback {
public:
    explicit OboeSinkStream(uint32_t system_channels, const char* name);
    ~OboeSinkStream() override;

    bool Initialize();
    void Finalize();
    void Start();
    void Stop();
    
    // 音频数据写入
    void WriteAudio(const float* data, int32_t num_frames);
    
    // 状态查询
    bool IsInitialized() const { return m_initialized.load(); }
    int32_t GetBufferedFrames() const;
    uint32_t GetSampleRate() const { return m_sample_rate; }
    uint32_t GetChannelCount() const { return m_device_channels; }
    
    // 音量控制
    void SetVolume(float volume);
    float GetVolume() const { return m_volume; }

    // Oboe 回调
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;
    void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    void onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) override;

private:
    bool OpenStream();
    bool ConfigureStream(oboe::AudioStreamBuilder& builder, oboe::Direction direction);
    static int32_t QueryChannelCount(oboe::Direction direction);
    
    // 环形缓冲区
    class RingBuffer {
    private:
        std::vector<float> m_buffer;
        std::atomic<size_t> m_read_index{0};
        std::atomic<size_t> m_write_index{0};
        size_t m_capacity;
        mutable std::mutex m_mutex;

    public:
        explicit RingBuffer(size_t capacity);
        bool Write(const float* data, size_t count);
        size_t Read(float* output, size_t count);
        size_t Available() const;
        size_t AvailableForWrite() const;
        void Clear();
    };

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<RingBuffer> m_ring_buffer;
    std::mutex m_stream_mutex;
    
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_paused{true};
    
    uint32_t m_system_channels;
    uint32_t m_device_channels;
    uint32_t m_sample_rate;
    std::string m_name;
    
    std::atomic<float> m_volume{1.0f};
    
    static constexpr uint32_t TARGET_SAMPLE_RATE = 48000;
    static constexpr uint32_t TARGET_SAMPLE_COUNT = 256;
};

class OboeAudioRenderer {
public:
    static OboeAudioRenderer& GetInstance();

    bool Initialize();
    void Shutdown();

    void SetSampleRate(int32_t sampleRate);
    void SetBufferSize(int32_t bufferSize);
    void SetVolume(float volume);

    void WriteAudio(const float* data, int32_t numFrames);
    void ClearBuffer();

    // 状态查询
    bool IsInitialized() const;
    int32_t GetBufferedFrames() const;
    uint32_t GetSampleRate() const;
    uint32_t GetChannelCount() const;

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    std::unique_ptr<OboeSinkStream> m_sink_stream;
    std::mutex m_init_mutex;
    std::atomic<bool> m_initialized{false};
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H