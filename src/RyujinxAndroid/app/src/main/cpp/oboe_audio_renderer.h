#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <thread>
#include <chrono>
#include <cstring>

namespace RyujinxOboe {

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

class RingBuffer {
public:
    RingBuffer(size_t capacity) 
        : capacity_(capacity), 
          buffer_(new uint8_t[capacity]),
          read_pos_(0),
          write_pos_(0),
          available_(0) {}
    
    ~RingBuffer() {
        delete[] buffer_;
    }
    
    size_t write(const void* data, size_t size) {
        std::lock_guard<std::mutex> lock(mutex_);
        
        if (size > capacity_ - available_) {
            size = capacity_ - available_;
            if (size == 0) return 0;
        }
        
        size_t first_chunk = std::min(size, capacity_ - write_pos_);
        std::memcpy(buffer_ + write_pos_, data, first_chunk);
        
        if (first_chunk < size) {
            std::memcpy(buffer_, static_cast<const uint8_t*>(data) + first_chunk, size - first_chunk);
        }
        
        write_pos_ = (write_pos_ + size) % capacity_;
        available_ += size;
        
        return size;
    }
    
    size_t read(void* data, size_t size) {
        std::lock_guard<std::mutex> lock(mutex_);
        
        if (size > available_) {
            size = available_;
            if (size == 0) return 0;
        }
        
        size_t first_chunk = std::min(size, capacity_ - read_pos_);
        std::memcpy(data, buffer_ + read_pos_, first_chunk);
        
        if (first_chunk < size) {
            std::memcpy(static_cast<uint8_t*>(data) + first_chunk, buffer_, size - first_chunk);
        }
        
        read_pos_ = (read_pos_ + size) % capacity_;
        available_ -= size;
        
        return size;
    }
    
    size_t available() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return available_;
    }
    
    size_t free() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return capacity_ - available_;
    }
    
    void clear() {
        std::lock_guard<std::mutex> lock(mutex_);
        read_pos_ = 0;
        write_pos_ = 0;
        available_ = 0;
    }
    
    size_t capacity() const { return capacity_; }

private:
    size_t capacity_;
    uint8_t* buffer_;
    size_t read_pos_;
    size_t write_pos_;
    size_t available_;
    mutable std::mutex mutex_;
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
    bool TryOpenStreamWithRetry(int maxRetryCount = 3);

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
    
    // 使用环形缓冲区替代队列
    static constexpr size_t RING_BUFFER_CAPACITY = 256 * 1024; // 256KB
    std::unique_ptr<RingBuffer> m_ring_buffer;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H