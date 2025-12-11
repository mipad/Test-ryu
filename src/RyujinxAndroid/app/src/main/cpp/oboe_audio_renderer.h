#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <atomic>
#include <memory>
#include <cstdint>
#include <thread>
#include <chrono>
#include <set>
#include <functional>
#include "LockFreeQueue.h"

namespace RyujinxOboe {

enum SampleFormat {
    PCM_INT16 = 1,
    PCM_INT24 = 2,
    PCM_INT32 = 3,
    PCM_FLOAT = 4
};

enum class StreamState {
    Uninitialized,
    Opening,
    Started,
    Stopping,
    Stopped,
    Paused,
    Flushed,
    Disconnected,
    Error,
    Closed
};

struct AudioBlock {
    static constexpr size_t BLOCK_SIZE = 4096;
    
    uint8_t data[BLOCK_SIZE];
    size_t data_size = 0;
    size_t data_played = 0;
    int32_t sample_format = PCM_INT16;
    bool consumed = true;
    
    void clear() {
        data_size = 0;
        data_played = 0;
        consumed = true;
    }
    
    size_t available() const {
        return data_size - data_played;
    }
};

struct PerformanceStats {
    std::atomic<int64_t> xrun_count{0};
    std::atomic<int64_t> total_frames_played{0};
    std::atomic<int64_t> total_frames_written{0};
    std::atomic<double> average_latency_ms{0.0};
    std::atomic<double> max_latency_ms{0.0};
    std::atomic<double> min_latency_ms{1000.0};
    std::atomic<int64_t> error_count{0};
    std::chrono::steady_clock::time_point last_error_time;
    
    // 自定义拷贝构造函数，用于复制atomic成员的值
    PerformanceStats() = default;
    PerformanceStats(const PerformanceStats& other) {
        xrun_count.store(other.xrun_count.load());
        total_frames_played.store(other.total_frames_played.load());
        total_frames_written.store(other.total_frames_written.load());
        average_latency_ms.store(other.average_latency_ms.load());
        max_latency_ms.store(other.max_latency_ms.load());
        min_latency_ms.store(other.min_latency_ms.load());
        error_count.store(other.error_count.load());
        last_error_time = other.last_error_time;
    }
    
    PerformanceStats& operator=(const PerformanceStats& other) {
        if (this != &other) {
            xrun_count.store(other.xrun_count.load());
            total_frames_played.store(other.total_frames_played.load());
            total_frames_written.store(other.total_frames_written.load());
            average_latency_ms.store(other.average_latency_ms.load());
            max_latency_ms.store(other.max_latency_ms.load());
            min_latency_ms.store(other.min_latency_ms.load());
            error_count.store(other.error_count.load());
            last_error_time = other.last_error_time;
        }
        return *this;
    }
    
    // 移动构造函数
    PerformanceStats(PerformanceStats&& other) noexcept {
        xrun_count.store(other.xrun_count.load());
        total_frames_played.store(other.total_frames_played.load());
        total_frames_written.store(other.total_frames_written.load());
        average_latency_ms.store(other.average_latency_ms.load());
        max_latency_ms.store(other.max_latency_ms.load());
        min_latency_ms.store(other.min_latency_ms.load());
        error_count.store(other.error_count.load());
        last_error_time = other.last_error_time;
    }
    
    PerformanceStats& operator=(PerformanceStats&& other) noexcept {
        if (this != &other) {
            xrun_count.store(other.xrun_count.load());
            total_frames_played.store(other.total_frames_played.load());
            total_frames_written.store(other.total_frames_written.load());
            average_latency_ms.store(other.average_latency_ms.load());
            max_latency_ms.store(other.max_latency_ms.load());
            min_latency_ms.store(other.min_latency_ms.load());
            error_count.store(other.error_count.load());
            last_error_time = other.last_error_time;
        }
        return *this;
    }
};

class OboeAudioRenderer {
public:
    using ErrorCallback = std::function<void(const std::string& error, oboe::Result result)>;
    using StateCallback = std::function<void(StreamState oldState, StreamState newState)>;
    
    OboeAudioRenderer();
    ~OboeAudioRenderer();

    bool Initialize(int32_t sampleRate, int32_t channelCount);
    bool InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat);
    void Shutdown();
    
    bool WriteAudio(const int16_t* data, int32_t num_frames);
    bool WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat);
    
    bool IsInitialized() const { return m_initialized.load(); }
    bool IsPlaying() const { 
        if (!m_stream) return false;
        auto state = m_stream->getState();
        return state == oboe::StreamState::Started || state == oboe::StreamState::Starting;
    }
    
    int32_t GetBufferedFrames() const;
    StreamState GetState() const { return m_current_state.load(); }
    PerformanceStats GetPerformanceStats() const;
    
    void SetVolume(float volume);
    float GetVolume() const { return m_volume.load(); }
    
    void Reset();
    void PreallocateBlocks(size_t count);
    
    void SetErrorCallback(ErrorCallback callback) { m_error_callback_user = std::move(callback); }
    void SetStateCallback(StateCallback callback) { m_state_callback_user = std::move(callback); }
    
    void SetPerformanceHintEnabled(bool enabled) { m_performance_hint_enabled = enabled; }
    bool IsPerformanceHintEnabled() const { return m_performance_hint_enabled; }
    
    double CalculateLatencyMillis();
    int32_t GetXRunCount();
    
    static size_t GetActiveInstanceCount();
    static void DumpAllInstancesInfo();
    
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

    struct AdpfWrapper {
        void* handle = nullptr;
        bool isOpen = false;
        bool attempted = false;
        
        bool open(pid_t tid, int64_t targetDurationNanos);
        void close();
        void onBeginCallback();
        void onEndCallback(double durationScaler);
    };

    bool OpenStream();
    void CloseStream();
    bool ConfigureAndOpenStream();
    void ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder);
    void ConfigureForOpenSLES(oboe::AudioStreamBuilder& builder);

    oboe::DataCallbackResult OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames);
    void OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error);
    void OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error);
    
    void HandleError(oboe::Result error, const std::string& context);
    void UpdateState(StreamState newState);
    void UpdateLatencyMeasurement();
    void UpdateXRunCount();
    
    bool RecoverFromError(oboe::Result error);
    bool TryFallbackApi();
    
    oboe::AudioFormat MapSampleFormat(int32_t format);
    static size_t GetBytesPerSample(int32_t format);
    bool OptimizeBufferSize();
    bool TryOpenStreamWithRetry(int maxRetryCount = 3);
    
    void BeginPerformanceHint();
    void EndPerformanceHint(int32_t num_frames);
    
    void RegisterInstance();
    void UnregisterInstance();

    std::shared_ptr<oboe::AudioStream> m_stream;
    std::unique_ptr<AAudioExclusiveCallback> m_audio_callback;
    std::unique_ptr<AAudioExclusiveErrorCallback> m_error_callback;
    
    std::mutex m_stream_mutex;
    mutable std::mutex m_stats_mutex;
    std::atomic<bool> m_initialized{false};
    std::atomic<bool> m_stream_started{false};
    std::atomic<bool> m_performance_hint_enabled{true};
    
    std::atomic<int32_t> m_sample_rate{48000};
    std::atomic<int32_t> m_channel_count{2};
    std::atomic<int32_t> m_sample_format{PCM_INT16};
    std::atomic<float> m_volume{1.0f};
    std::atomic<StreamState> m_current_state{StreamState::Uninitialized};
    
    int32_t m_device_channels = 2;
    oboe::AudioFormat m_oboe_format{oboe::AudioFormat::I16};
    oboe::AudioApi m_current_api{oboe::AudioApi::Unspecified};
    
    PerformanceStats m_performance_stats;
    
    ErrorCallback m_error_callback_user;
    StateCallback m_state_callback_user;
    
    AdpfWrapper m_adpf_wrapper;
    
    static constexpr uint32_t AUDIO_QUEUE_SIZE = 512;
    static constexpr uint32_t OBJECT_POOL_SIZE = 4096;
    
    LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE> m_audio_queue;
    LockFreeObjectPool<AudioBlock, OBJECT_POOL_SIZE> m_object_pool;
    
    std::unique_ptr<AudioBlock> m_current_block;
    
    static std::mutex s_instances_mutex;
    static std::set<OboeAudioRenderer*> s_active_instances;
};

} // namespace RyujinxOboe

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
