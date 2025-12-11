#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <thread>
#include <chrono>
#include <deque>
#include <vector>

namespace RyujinxOboe {

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

struct AudioBlock {
    static constexpr size_t DEFAULT_BLOCK_SIZE = 4096;
    
    std::vector<uint8_t> data;
    size_t data_size = 0;
    size_t data_played = 0;
    int32_t sample_format = PCM_INT16;
    bool consumed = true;
    
    AudioBlock() : data(DEFAULT_BLOCK_SIZE) {}
    
    void clear() {
        data_size = 0;
        data_played = 0;
        consumed = true;
    }
    
    size_t available() const {
        return data_size - data_played;
    }
    
    void ensure_capacity(size_t required_size) {
        if (data.size() < required_size) {
            // 扩容到所需大小的下一个2的幂次方
            size_t new_size = 1;
            while (new_size < required_size) {
                new_size <<= 1;
            }
            data.resize(new_size);
        }
    }
};

// 简化的动态音频队列
class DynamicAudioQueue {
private:
    std::deque<std::unique_ptr<AudioBlock>> m_queue;
    mutable std::mutex m_mutex;
    
    // 统计信息
    std::atomic<size_t> m_total_pushed{0};
    std::atomic<size_t> m_total_popped{0};
    std::atomic<size_t> m_max_size{0};
    std::atomic<size_t> m_dropped_blocks{0};
    
    // 容量限制（用于统计）
    size_t m_capacity_hint{0};
    
public:
    DynamicAudioQueue() = default;
    
    bool push(std::unique_ptr<AudioBlock> block) {
        std::lock_guard<std::mutex> lock(m_mutex);
        
        // 更新统计信息
        m_queue.push_back(std::move(block));
        m_total_pushed.fetch_add(1);
        
        // 更新最大队列大小
        size_t current_size = m_queue.size();
        size_t max_size = m_max_size.load();
        while (current_size > max_size) {
            if (m_max_size.compare_exchange_weak(max_size, current_size)) {
                break;
            }
        }
        
        return true;
    }
    
    bool pop(std::unique_ptr<AudioBlock>& block) {
        std::lock_guard<std::mutex> lock(m_mutex);
        
        if (m_queue.empty()) {
            return false;
        }
        
        block = std::move(m_queue.front());
        m_queue.pop_front();
        m_total_popped.fetch_add(1);
        return true;
    }
    
    bool empty() const {
        std::lock_guard<std::mutex> lock(m_mutex);
        return m_queue.empty();
    }
    
    size_t size() const {
        std::lock_guard<std::mutex> lock(m_mutex);
        return m_queue.size();
    }
    
    void clear() {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_queue.clear();
    }
    
    // 统计信息获取
    size_t get_total_pushed() const { return m_total_pushed.load(); }
    size_t get_total_popped() const { return m_total_popped.load(); }
    size_t get_max_size() const { return m_max_size.load(); }
    size_t get_dropped_blocks() const { return m_dropped_blocks.load(); }
    
    void enable_dynamic_growth(bool enable) {
        // 对于std::deque，总是动态增长
    }
    
    void reserve(size_t capacity) {
        // std::deque 不支持 reserve，我们只记录容量提示
        m_capacity_hint = capacity;
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
    
    // 队列统计
    size_t GetQueueSize() const { return m_audio_queue.size(); }
    size_t GetMaxQueueSize() const { return m_audio_queue.get_max_size(); }
    size_t GetTotalPushedBlocks() const { return m_audio_queue.get_total_pushed(); }
    size_t GetTotalPoppedBlocks() const { return m_audio_queue.get_total_popped(); }

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
    
    // 辅助函数
    std::unique_ptr<AudioBlock> create_audio_block();
    void return_audio_block(std::unique_ptr<AudioBlock> block);

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
    
    // 动态音频队列
    DynamicAudioQueue m_audio_queue;
    
    std::unique_ptr<AudioBlock> m_current_block;
    
    // 对象池
    static constexpr size_t BLOCK_POOL_SIZE = 256;
    std::vector<std::unique_ptr<AudioBlock>> m_block_pool;
    std::atomic<size_t> m_block_pool_used{0};
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
