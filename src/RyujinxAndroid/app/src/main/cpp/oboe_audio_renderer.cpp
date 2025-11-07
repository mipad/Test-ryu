// oboe_audio_renderer.cpp (混合模式版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

// =============== HybridRingBuffer Implementation ===============
OboeAudioRenderer::HybridRingBuffer::HybridRingBuffer(size_t capacity, int32_t channels) 
    : m_samples_capacity(capacity * channels),
      m_buffer(m_samples_capacity),
      m_capacity(capacity),
      m_channels(channels) {
    LOGI("HybridRingBuffer: %zu frames, %d channels, %zu samples", 
         capacity, channels, m_samples_capacity);
}

OboeAudioRenderer::HybridRingBuffer::~HybridRingBuffer() {
    Clear();
}

bool OboeAudioRenderer::HybridRingBuffer::Write(const int16_t* data, size_t frames) {
    if (!data || frames == 0) return false;
    
    size_t samples_needed = frames * m_channels;
    
    // 检查是否有足够空间
    if (GetFreeSpace() < samples_needed) {
        return false;
    }
    
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    
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

size_t OboeAudioRenderer::HybridRingBuffer::Read(int16_t* output, size_t frames) {
    if (!output || frames == 0) return 0;
    
    size_t samples_requested = frames * m_channels;
    size_t available = Available() * m_channels;
    
    size_t samples_to_read = std::min(samples_requested, available);
    if (samples_to_read == 0) {
        return 0;
    }
    
    // 确保读取的样本数是声道数的整数倍
    samples_to_read = (samples_to_read / m_channels) * m_channels;
    if (samples_to_read == 0) {
        return 0;
    }
    
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
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

size_t OboeAudioRenderer::HybridRingBuffer::Available() const {
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    if (write_pos >= read_pos) {
        return (write_pos - read_pos) / m_channels;
    } else {
        return (m_samples_capacity - read_pos + write_pos) / m_channels;
    }
}

size_t OboeAudioRenderer::HybridRingBuffer::GetFreeSpace() const {
    size_t available = Available();
    return m_capacity - available - 1; // 保留一个样本避免完全填满
}

void OboeAudioRenderer::HybridRingBuffer::Clear() {
    m_read_pos.store(0, std::memory_order_release);
    m_write_pos.store(0, std::memory_order_release);
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::HybridAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::HybridErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Audio stream closed with error: %d", error);
    m_renderer->OnStreamError(error);
}

void OboeAudioRenderer::HybridErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error before close: %d", error);
    m_renderer->OnStreamError(error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<HybridAudioCallback>(this);
    m_error_callback = std::make_unique<HybridErrorCallback>(this);
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
    
    // 根据声道数选择模式
    if (channelCount == 6) {
        m_current_mode = "High-Performance (6-channel)";
        LOGI("Initializing Oboe in HIGH-PERFORMANCE mode: %dHz %dch", sampleRate, channelCount);
    } else {
        m_current_mode = "Stable Mode";
        LOGI("Initializing Oboe in STABLE mode: %dHz %dch", sampleRate, channelCount);
    }
    
    // 根据模式计算缓冲区大小
    size_t buffer_duration_ms = (channelCount == 6) ? BUFFER_DURATION_HIGH_PERF_MS : BUFFER_DURATION_STABLE_MS;
    size_t buffer_capacity = (sampleRate * buffer_duration_ms) / 1000;
    m_ring_buffer = std::make_unique<HybridRingBuffer>(buffer_capacity, channelCount);
    
    if (!ConfigureAndOpenStream()) {
        LOGE("Failed to open audio stream in %s mode", m_current_mode.c_str());
        return false;
    }
    
    m_initialized.store(true);
    LOGI("OboeAudioRenderer %s initialization complete", m_current_mode.c_str());
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

void OboeAudioRenderer::ConfigureForHybridMode(oboe::AudioStreamBuilder& builder) {
    int32_t channelCount = m_channel_count.load();
    
    // 始终使用AAudio
    builder.setAudioApi(oboe::AudioApi::AAudio)
           ->setPerformanceMode(PERFORMANCE_MODE)
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(channelCount)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormatConversionAllowed(true)
           ->setChannelConversionAllowed(true);
    
    // 根据声道数调整参数
    if (channelCount == 6) {
        // 6声道: 高性能模式 - 独占模式，较小的回调
        builder.setSharingMode(oboe::SharingMode::Exclusive)
               ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK_HIGH_PERF);
    } else {
        // 其他声道: 稳定模式 - 共享模式，较大的回调
        builder.setSharingMode(oboe::SharingMode::Shared)
               ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK_STABLE);
    }
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置混合模式参数
    ConfigureForHybridMode(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 始终使用AAudio
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        LOGE("Failed to open AAudio stream: %d", result);
        return false;
    }
    
    // 根据模式设置缓冲区大小
    int32_t channelCount = m_channel_count.load();
    int32_t target_frames_per_callback = (channelCount == 6) ? 
        TARGET_FRAMES_PER_CALLBACK_HIGH_PERF : TARGET_FRAMES_PER_CALLBACK_STABLE;
    
    int32_t desired_buffer_size = target_frames_per_callback * 4;
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
    LOGI("Audio stream [%s]: AAudio, %d channels, %d Hz, %s mode, buffer: %d/%d frames",
         m_current_mode.c_str(),
         m_stream->getChannelCount(),
         m_stream->getSampleRate(),
         m_stream->getSharingMode() == oboe::SharingMode::Exclusive ? "exclusive" : "shared",
         m_stream->getBufferSizeInFrames(),
         m_stream->getBufferCapacityInFrames());
    
    return true;
}

bool OboeAudioRenderer::OpenStream() {
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
        m_write_failures++;
        return false;
    }
    
    if (!m_ring_buffer) {
        m_write_failures++;
        return false;
    }
    
    // 检查是否有足够空间
    if (m_ring_buffer->GetFreeSpace() < static_cast<size_t>(num_frames)) {
        m_buffer_overflows++;
        LOGW("Audio buffer overflow in %s mode: %d frames requested, %zu free", 
             m_current_mode.c_str(), num_frames, m_ring_buffer->GetFreeSpace());
        return false;
    }
    
    int32_t channelCount = m_channel_count.load();
    
    // 根据模式选择音量处理策略
    float volume = m_volume.load();
    bool apply_volume = (volume != 1.0f);
    
    bool success;
    
    if (apply_volume) {
        // 需要应用音量，创建临时缓冲区
        std::vector<int16_t> volume_adjusted(num_frames * channelCount);
        for (int32_t i = 0; i < num_frames * channelCount; i++) {
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
        m_write_failures++;
        LOGW("Audio write failed in %s mode: %d frames", m_current_mode.c_str(), num_frames);
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
    
    LOGI("Resetting audio stream in %s mode", m_current_mode.c_str());
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
    }
    
    // 重新配置和打开流
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
    
    // 记录统计信息
    PerformanceStats stats = GetStats();
    LOGI("Audio stream reset (count: %d). Mode: %s, failures=%d, overflows=%d", 
         m_stream_restart_count.load(), m_current_mode.c_str(), 
         stats.write_failures, stats.buffer_overflows);
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
        
        static int underflow_log_counter = 0;
        if (++underflow_log_counter >= 10) {
            LOGW("Audio underflow in %s mode: %zu/%d frames available", 
                 m_current_mode.c_str(), frames_read, num_frames);
            underflow_log_counter = 0;
        }
    }
    
    // 注意：音量调整现在在WriteAudio中进行，所以这里不需要再次调整
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::Result error) {
    LOGW("Stream error in %s mode: %d", m_current_mode.c_str(), error);
    
    // 在混合模式下，我们让上层逻辑决定何时重置
}

OboeAudioRenderer::PerformanceStats OboeAudioRenderer::GetStats() const {
    PerformanceStats stats;
    stats.frames_written = m_frames_written.load();
    stats.frames_played = m_frames_played.load();
    stats.write_failures = m_write_failures.load();
    stats.stream_restart_count = m_stream_restart_count.load();
    stats.buffer_overflows = m_buffer_overflows.load();
    stats.mode = m_current_mode;
    return stats;
}

} // namespace RyujinxOboe