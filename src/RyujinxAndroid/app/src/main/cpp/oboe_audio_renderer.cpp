// oboe_audio_renderer.cpp (修复版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

// =============== LockFreeRingBuffer Implementation ===============
OboeAudioRenderer::LockFreeRingBuffer::LockFreeRingBuffer(size_t capacity, int32_t channels) 
    : m_samples_capacity(capacity * channels),  // 先初始化
      m_buffer(m_samples_capacity),             // 然后使用
      m_capacity(capacity),
      m_channels(channels) {
    LOGI("LockFreeRingBuffer: %zu frames, %d channels, %zu samples", 
         capacity, channels, m_samples_capacity);
}

OboeAudioRenderer::LockFreeRingBuffer::~LockFreeRingBuffer() {
    Clear();
}

bool OboeAudioRenderer::LockFreeRingBuffer::Write(const int16_t* data, size_t frames) {
    if (!data || frames == 0) return false;
    
    size_t samples_needed = frames * m_channels;
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    // 计算可用空间 (无锁算法)
    size_t available_space;
    if (write_pos >= read_pos) {
        available_space = m_samples_capacity - write_pos + read_pos;
    } else {
        available_space = read_pos - write_pos;
    }
    
    // 检查是否有足够空间 (保留少量空间避免边界情况)
    if (available_space <= samples_needed + m_channels) {
        m_write_pos.store(write_pos, std::memory_order_release);
        return false;
    }
    
    // 写入数据
    size_t end_pos = write_pos + samples_needed;
    if (end_pos <= m_samples_capacity) {
        std::memcpy(&m_buffer[write_pos], data, samples_needed * sizeof(int16_t));
    } else {
        size_t first_part = m_samples_capacity - write_pos;
        std::memcpy(&m_buffer[write_pos], data, first_part * sizeof(int16_t));
        std::memcpy(&m_buffer[0], data + first_part, (samples_needed - first_part) * sizeof(int16_t));
    }
    
    m_write_pos.store((write_pos + samples_needed) % m_samples_capacity, std::memory_order_release);
    return true;
}

size_t OboeAudioRenderer::LockFreeRingBuffer::Read(int16_t* output, size_t frames) {
    if (!output || frames == 0) return 0;
    
    size_t samples_requested = frames * m_channels;
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    // 计算可用数据 (无锁算法)
    size_t available_data;
    if (write_pos >= read_pos) {
        available_data = write_pos - read_pos;
    } else {
        available_data = m_samples_capacity - read_pos + write_pos;
    }
    
    size_t samples_to_read = std::min(samples_requested, available_data);
    if (samples_to_read == 0) {
        return 0;
    }
    
    // 确保读取的样本数是声道数的整数倍
    samples_to_read = (samples_to_read / m_channels) * m_channels;
    if (samples_to_read == 0) {
        return 0;
    }
    
    // 读取数据
    size_t end_pos = read_pos + samples_to_read;
    if (end_pos <= m_samples_capacity) {
        std::memcpy(output, &m_buffer[read_pos], samples_to_read * sizeof(int16_t));
    } else {
        size_t first_part = m_samples_capacity - read_pos;
        std::memcpy(output, &m_buffer[read_pos], first_part * sizeof(int16_t));
        std::memcpy(output + first_part, &m_buffer[0], (samples_to_read - first_part) * sizeof(int16_t));
    }
    
    m_read_pos.store((read_pos + samples_to_read) % m_samples_capacity, std::memory_order_release);
    return samples_to_read / m_channels;
}

size_t OboeAudioRenderer::LockFreeRingBuffer::Available() const {
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    size_t available_samples;
    if (write_pos >= read_pos) {
        available_samples = write_pos - read_pos;
    } else {
        available_samples = m_samples_capacity - read_pos + write_pos;
    }
    
    return available_samples / m_channels;
}

void OboeAudioRenderer::LockFreeRingBuffer::Clear() {
    m_read_pos.store(0, std::memory_order_release);
    m_write_pos.store(0, std::memory_order_release);
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::HighPerformanceAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::HighPerformanceErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Audio stream closed with error: %d", error);
    m_renderer->OnStreamError(error);
}

void OboeAudioRenderer::HighPerformanceErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error before close: %d", error);
    m_renderer->OnStreamError(error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<HighPerformanceAudioCallback>(this);
    m_error_callback = std::make_unique<HighPerformanceErrorCallback>(this);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::GetInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    if (m_initialized.load()) {
        // 检查是否需要重新初始化
        if (m_sample_rate.load() != sampleRate || m_channel_count.load() != channelCount) {
            LOGI("Audio parameters changed: %dHz %dch -> %dHz %dch", 
                 m_sample_rate.load(), m_channel_count.load(), sampleRate, channelCount);
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    
    LOGI("Initializing Oboe: %dHz %dch", sampleRate, channelCount);
    
    // 计算优化的缓冲区大小
    size_t buffer_capacity = (sampleRate * BUFFER_DURATION_MS) / 1000;
    m_ring_buffer = std::make_unique<LockFreeRingBuffer>(buffer_capacity, channelCount);
    
    if (!ConfigureAndOpenStream()) {
        LOGE("Failed to open optimized audio stream");
        return false;
    }
    
    m_initialized.store(true);
    LOGI("OboeAudioRenderer optimized initialization complete");
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
        m_ring_buffer.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
    LOGI("OboeAudioRenderer shutdown");
}

void OboeAudioRenderer::ConfigureForPerformance(oboe::AudioStreamBuilder& builder) {
    // 高性能配置 - 直接修改传入的builder
    builder.setPerformanceMode(PERFORMANCE_MODE)
           ->setSharingMode(SHARING_MODE)  // 独占模式，更好性能
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(m_channel_count.load())
           ->setSampleRate(m_sample_rate.load())
           ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium) // 平衡质量和性能
           ->setFormatConversionAllowed(true)
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置高性能参数
    ConfigureForPerformance(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 策略：优先尝试AAudio独占模式，如果失败则回退
    bool success = false;
    
    // 尝试1: AAudio + 独占模式 (最佳性能)
    builder.setAudioApi(oboe::AudioApi::AAudio);
    auto result = builder.openStream(m_stream);
    
    if (result == oboe::Result::OK) {
        LOGI("Using AAudio exclusive mode - optimal performance");
        success = true;
    } else {
        LOGW("AAudio exclusive failed, trying AAudio shared mode");
        
        // 尝试2: AAudio + 共享模式
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(m_stream);
        
        if (result == oboe::Result::OK) {
            LOGI("Using AAudio shared mode");
            success = true;
        } else {
            LOGW("AAudio shared failed, trying OpenSLES");
            
            // 尝试3: OpenSLES
            builder.setAudioApi(oboe::AudioApi::OpenSLES)
                   ->setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
            
            if (result == oboe::Result::OK) {
                LOGI("Using OpenSLES shared mode");
                success = true;
            }
        }
    }
    
    if (!success) {
        LOGE("All audio API attempts failed");
        return false;
    }
    
    // 优化缓冲区大小
    int32_t desired_buffer_size = TARGET_FRAMES_PER_CALLBACK * 2;
    auto setBufferResult = m_stream->setBufferSizeInFrames(desired_buffer_size);
    if (setBufferResult) {
        LOGD("Buffer size set to %d frames", setBufferResult.value());
    }
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start audio stream: %d", result);
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    
    // 记录流信息
    LOGI("Audio stream: %s, %d channels, %d Hz, %s mode, buffer: %d/%d frames",
         m_stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
         m_stream->getChannelCount(),
         m_stream->getSampleRate(),
         m_stream->getSharingMode() == oboe::SharingMode::Exclusive ? "exclusive" : "shared",
         m_stream->getBufferSizeInFrames(),
         m_stream->getBufferCapacityInFrames());
    
    return true;
}

bool OboeAudioRenderer::OpenStream() {
    // 统一使用 ConfigureAndOpenStream
    return ConfigureAndOpenStream();
}

void OboeAudioRenderer::CloseStream() {
    if (m_stream) {
        if (m_stream_started.load()) {
            m_stream->stop();
        }
        m_stream->close();
        m_stream.reset();
        m_stream_started.store(false);
    }
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    if (!m_ring_buffer) {
        return false;
    }
    
    // 应用音量 (在写入时应用，避免回调中计算)
    float volume = m_volume.load();
    bool apply_volume = (volume != 1.0f);
    
    bool success;
    
    if (apply_volume) {
        // 需要应用音量，创建临时缓冲区
        std::vector<int16_t> volume_adjusted(num_frames * m_channel_count.load());
        for (int32_t i = 0; i < num_frames * m_channel_count.load(); i++) {
            volume_adjusted[i] = static_cast<int16_t>(data[i] * volume);
        }
        success = m_ring_buffer->Write(volume_adjusted.data(), num_frames);
    } else {
        // 直接写入，无音量调整
        success = m_ring_buffer->Write(data, num_frames);
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
        LOGD("Audio buffer overflow: %d frames dropped", num_frames);
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return m_ring_buffer ? static_cast<int32_t>(m_ring_buffer->Available()) : 0;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
    }
    
    // 重新配置和打开流
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
    LOGI("Audio stream reset (count: %d)", m_stream_restart_count.load());
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_ring_buffer) {
        // 输出静音
        int32_t channels = m_channel_count.load();
        std::memset(audioData, 0, num_frames * channels * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }
    
    int16_t* output = static_cast<int16_t*>(audioData);
    int32_t channels = m_channel_count.load();
    
    // 从环形缓冲区读取数据
    size_t frames_read = m_ring_buffer->Read(output, num_frames);
    
    // 如果数据不足，填充静音
    if (frames_read < static_cast<size_t>(num_frames)) {
        size_t samples_remaining = (num_frames - frames_read) * channels;
        std::memset(output + (frames_read * channels), 0, samples_remaining * sizeof(int16_t));
        
        // 减少underflow日志频率，避免性能影响
        static int underflow_log_counter = 0;
        if (++underflow_log_counter >= 10) {
            LOGW("Audio underflow: %zu/%d frames available", frames_read, num_frames);
            underflow_log_counter = 0;
        }
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::Result error) {
    LOGW("Stream error: %d", error);
    
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false);
    
    // 延迟恢复，避免频繁重启
    static auto last_recovery_time = std::chrono::steady_clock::now();
    auto now = std::chrono::steady_clock::now();
    auto time_since_last_recovery = std::chrono::duration_cast<std::chrono::milliseconds>(now - last_recovery_time);
    
    if (time_since_last_recovery > std::chrono::milliseconds(1000)) { // 至少1秒后再恢复
        if (m_initialized.load()) {
            LOGI("Recovering audio stream");
            CloseStream();
            ConfigureAndOpenStream();
            last_recovery_time = now;
        }
    }
}

OboeAudioRenderer::PerformanceStats OboeAudioRenderer::GetStats() const {
    PerformanceStats stats;
    stats.frames_written = m_frames_written.load();
    stats.frames_played = m_frames_played.load();
    stats.underrun_count = m_underrun_count.load();
    stats.stream_restart_count = m_stream_restart_count.load();
    return stats;
}

} // namespace RyujinxOboe