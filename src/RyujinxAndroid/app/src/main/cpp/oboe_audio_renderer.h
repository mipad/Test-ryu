#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <thread>
#include <chrono>
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
            data.resize(required_size);
        }
    }
};

// 简单的动态队列实现
class DynamicAudioQueue {
private:
    struct Node {
        std::unique_ptr<AudioBlock> block;
        std::unique_ptr<Node> next;
    };
    
    std::unique_ptr<Node> m_head;
    std::unique_ptr<Node> m_tail;
    Node* m_head_ptr = nullptr;
    Node* m_tail_ptr = nullptr;
    
    std::atomic<size_t> m_size{0};
    std::mutex m_mutex;
    
    std::unique_ptr<Node> create_node() {
        return std::make_unique<Node>();
    }
    
public:
    DynamicAudioQueue() {
        m_head = create_node();
        m_tail = m_head.get();
        m_head_ptr = m_head.get();
        m_tail_ptr = m_head.get();
    }
    
    bool push(std::unique_ptr<AudioBlock> block) {
        std::lock_guard<std::mutex> lock(m_mutex);
        
        // 写入数据到尾部节点
        std::swap(m_tail_ptr->block, block);
        
        // 创建新节点作为下一个
        m_tail_ptr->next = create_node();
        m_tail_ptr = m_tail_ptr->next.get();
        
        m_size.fetch_add(1);
        return true;
    }
    
    bool pop(std::unique_ptr<AudioBlock>& block) {
        std::lock_guard<std::mutex> lock(m_mutex);
        
        if (!m_head_ptr->block) {
            return false;
        }
        
        std::swap(m_head_ptr->block, block);
        
        // 移动到下一个节点
        if (m_head_ptr->next) {
            m_head_ptr = m_head_ptr->next.get();
        } else {
            // 如果没有下一个节点，创建一个新的
            m_head_ptr->next = create_node();
            m_head_ptr = m_head_ptr->next.get();
        }
        
        m_size.fetch_sub(1);
        return true;
    }
    
    size_t size() const {
        return m_size.load();
    }
    
    void clear() {
        std::lock_guard<std::mutex> lock(m_mutex);
        
        // 重置队列
        m_head = create_node();
        m_tail = m_head.get();
        m_head_ptr = m_head.get();
        m_tail_ptr = m_head.get();
        m_size.store(0);
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