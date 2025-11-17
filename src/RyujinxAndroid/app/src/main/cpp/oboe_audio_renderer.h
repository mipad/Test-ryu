// oboe_audio_renderer.h (支持所有采样率，带内存池优化和对齐保证)
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
#include <string>
#include <array>

// 前向声明
namespace RyujinxOboe {
    class StabilizedAudioCallback;
}

namespace RyujinxOboe {

// 采样格式定义 (与C#层保持一致)
enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

// 内存池配置
constexpr size_t AUDIO_POOL_SIZE = 32;
constexpr size_t MAX_AUDIO_FRAME_SIZE = 1024 * 8 * 4; // 1024帧 * 8声道 * 4字节
constexpr size_t ALIGNMENT = 16; // 16字节对齐，适合ARM NEON

// 对齐的内存分配器
class AlignedMemory {
public:
    static void* AllocateAligned(size_t size, size_t alignment = ALIGNMENT) {
#if defined(_WIN32)
        return _aligned_malloc(size, alignment);
#else
        void* ptr = nullptr;
        if (posix_memalign(&ptr, alignment, size) != 0) {
            return nullptr;
        }
        return ptr;
#endif
    }
    
    static void FreeAligned(void* ptr) {
#if defined(_WIN32)
        _aligned_free(ptr);
#else
        free(ptr);
#endif
    }
    
    static bool IsAligned(const void* ptr, size_t alignment = ALIGNMENT) {
        return (reinterpret_cast<uintptr_t>(ptr) % alignment) == 0;
    }
};

// 对齐的音频内存块
struct alignas(ALIGNMENT) AudioMemoryBlock {
    alignas(ALIGNMENT) uint8_t data[MAX_AUDIO_FRAME_SIZE];
    size_t used_size = 0;
    bool in_use = false;
    int32_t sample_format = 1;
    
    // 对齐检查方法
    bool isDataAligned() const {
        return AlignedMemory::IsAligned(data);
    }
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

    // 稳定回调控制
    void SetStabilizedCallbackEnabled(bool enabled);
    bool IsStabilizedCallbackEnabled() const { return m_stabilized_callback_enabled.load(); }
    void SetStabilizedCallbackIntensity(float intensity);
    float GetStabilizedCallbackIntensity() const { return m_stabilized_callback_intensity.load(); }

private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    // 基于内存池的音频缓冲区
    struct PooledAudioBuffer {
        AudioMemoryBlock* block = nullptr;
        size_t data_played = 0;
        bool consumed = true;
    };

    // 简单音频内存池
    class AudioMemoryPool {
    public:
        AudioMemoryPool(size_t pool_size = AUDIO_POOL_SIZE);
        ~AudioMemoryPool();
        
        AudioMemoryBlock* AllocateBlock();
        void ReleaseBlock(AudioMemoryBlock* block);
        void Clear();
        size_t GetFreeBlockCount() const;

    private:
        std::vector<AudioMemoryBlock> m_blocks;
        std::vector<AudioMemoryBlock*> m_free_blocks;
        mutable std::mutex m_pool_mutex;
    };

    // 原始格式缓冲区结构（使用内存池）
    class PooledAudioBufferQueue {
    public:
        explicit PooledAudioBufferQueue(std::shared_ptr<AudioMemoryPool> pool, size_t max_buffers = 32);
        ~PooledAudioBufferQueue();
        
        bool WriteRaw(const uint8_t* data, size_t data_size, int32_t sample_format);
        size_t ReadRaw(uint8_t* output, size_t output_size, int32_t target_format);
        size_t Available() const;
        void Clear();
        
        int32_t GetCurrentFormat() const { return m_current_format; }
        
    private:
        std::queue<PooledAudioBuffer> m_buffers;
        PooledAudioBuffer m_playing_buffer;
        size_t m_max_buffers;
        mutable std::mutex m_mutex;
        int32_t m_current_format = 1; // PCM16 by default
        std::shared_ptr<AudioMemoryPool> m_memory_pool;
        
        // 对齐的内存拷贝函数
        void CopyAlignedMemory(void* dst, const void* src, size_t size);
    };

    class AAudioExclusiveCallback : public oboe::AudioStreamDataCallback {
    public:
        explicit AAudioExclusiveCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) override;

    private:
        OboeAudioRenderer* m_renderer;
    };

    class OboeErrorCallback : public oboe::AudioStreamErrorCallback {
    public:
        explicit OboeErrorCallback(OboeAudioRenderer* renderer) : m_renderer(renderer) {}
        
        void onErrorAfterClose(oboe::AudioStream *oboeStream, oboe::Result error) override {
            // 只在设备断开等可恢复错误时重启
            if (error == oboe::Result::ErrorDisconnected) {
                m_renderer->RestartStream();
            }
        }
        
        bool onError(oboe::AudioStream* audioStream, oboe::Result error) override {
            // 返回 false 让 Oboe 执行默认错误处理
            return false;
        }

    private:
        OboeAudioRenderer* m_renderer;
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);
    bool RestartStream();

    oboe::DataCallbackResult OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamError(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);

    // 格式转换函数
    oboe::AudioFormat MapSampleFormat(int32_t format);
    const char* GetFormatName(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    
    // 缓冲区优化
    bool OptimizeBufferSize();
    
    // 对齐的内存操作
    void FillSilenceAligned(void* data, size_t size_in_bytes);

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::shared_ptr<AudioMemoryPool> m_memory_pool;
    std::unique_ptr<PooledAudioBufferQueue> m_raw_sample_queue;
    std::shared_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::shared_ptr<OboeErrorCallback> m_error_callback;
    std::shared_ptr<StabilizedAudioCallback> m_stabilized_callback;
    
    std::mutex m_stream_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_stabilized_callback_enabled{true}; // 默认开启
    std::atomic<float> m_stabilized_callback_intensity{0.1f}; // 降低默认强度
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{1}; // PCM16 by default
    std::atomic<float> m_volume{1.0f};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    
    // 性能统计
    std::atomic<int32_t> m_underrun_count{0};
    
    std::string m_current_audio_api = "Unknown";
    std::string m_current_sharing_mode = "Unknown";
    std::string m_current_sample_format = "PCM16";
    
    static constexpr int32_t TARGET_SAMPLE_COUNT = 240;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H