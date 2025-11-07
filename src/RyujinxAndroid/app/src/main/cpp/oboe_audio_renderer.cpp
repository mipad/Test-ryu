// oboe_audio_renderer.cpp (音频拉伸版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

// =============== AudioStretchBuffer Implementation ===============
OboeAudioRenderer::AudioStretchBuffer::AudioStretchBuffer(size_t capacity, int32_t channels) 
    : m_samples_capacity(capacity * channels),
      m_buffer(m_samples_capacity),
      m_capacity(capacity),
      m_channels(channels),
      m_stretch_history(m_history_size * channels, 0) {
}

OboeAudioRenderer::AudioStretchBuffer::~AudioStretchBuffer() {
    Clear();
}

bool OboeAudioRenderer::AudioStretchBuffer::Write(const int16_t* data, size_t frames) {
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

size_t OboeAudioRenderer::AudioStretchBuffer::Read(int16_t* output, size_t frames) {
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

size_t OboeAudioRenderer::AudioStretchBuffer::ReadWithStretch(int16_t* output, size_t requested_frames, float stretch_factor) {
    if (!output || requested_frames == 0) return 0;
    
    size_t available = Available();
    
    if (available >= requested_frames || !m_stretch_enabled) {
        // 数据充足或拉伸禁用，正常读取
        m_stretch_active = false;
        return Read(output, requested_frames);
    }
    
    // 启用音频拉伸
    m_stretch_active = true;
    return ApplyTimeStretch(output, requested_frames, stretch_factor);
}

size_t OboeAudioRenderer::AudioStretchBuffer::ApplyTimeStretch(int16_t* output, size_t requested_frames, float stretch_factor) {
    // 简单的重叠相加时间拉伸算法
    // 这个实现会降低音频播放速度来避免卡顿
    
    size_t available = Available();
    if (available == 0) {
        // 完全没有数据，输出静音
        std::memset(output, 0, requested_frames * m_channels * sizeof(int16_t));
        return 0;
    }
    
    // 计算实际可以读取的帧数（减少读取以降低播放速度）
    size_t frames_to_read = static_cast<size_t>(available * stretch_factor);
    frames_to_read = std::min(frames_to_read, available);
    frames_to_read = std::min(frames_to_read, requested_frames);
    
    if (frames_to_read == 0) {
        std::memset(output, 0, requested_frames * m_channels * sizeof(int16_t));
        return 0;
    }
    
    // 正常读取部分数据
    size_t actual_read = Read(output, frames_to_read);
    
    if (actual_read < requested_frames) {
        // 如果读取的帧数不足，使用重叠相加填充剩余部分
        size_t remaining_frames = requested_frames - actual_read;
        
        if (actual_read > 0) {
            // 使用最后几帧进行重叠相加
            size_t overlap_frames = std::min(actual_read, remaining_frames);
            size_t overlap_samples = overlap_frames * m_channels;
            
            // 简单的淡入淡出重叠
            for (size_t i = 0; i < overlap_samples; i++) {
                size_t output_idx = actual_read * m_channels + i;
                size_t source_idx = (actual_read - overlap_frames) * m_channels + i;
                
                if (output_idx < requested_frames * m_channels) {
                    float fade_out = 1.0f - (static_cast<float>(i) / overlap_samples);
                    float fade_in = static_cast<float>(i) / overlap_samples;
                    
                    output[output_idx] = static_cast<int16_t>(
                        output[source_idx] * fade_out + 
                        output[output_idx - overlap_samples] * fade_in
                    );
                }
            }
            
            // 如果还有剩余，重复最后一帧
            if (remaining_frames > overlap_frames) {
                size_t repeat_start = actual_read * m_channels + overlap_samples;
                size_t repeat_from = (actual_read - 1) * m_channels;
                size_t repeat_count = remaining_frames - overlap_frames;
                
                for (size_t i = 0; i < repeat_count * m_channels; i++) {
                    if (repeat_start + i < requested_frames * m_channels) {
                        output[repeat_start + i] = output[repeat_from + (i % m_channels)];
                    }
                }
            }
        } else {
            // 完全没有读取到数据，输出静音
            std::memset(output, 0, requested_frames * m_channels * sizeof(int16_t));
        }
    }
    
    return requested_frames; // 总是返回请求的帧数
}

size_t OboeAudioRenderer::AudioStretchBuffer::Available() const {
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    if (write_pos >= read_pos) {
        return (write_pos - read_pos) / m_channels;
    } else {
        return (m_samples_capacity - read_pos + write_pos) / m_channels;
    }
}

size_t OboeAudioRenderer::AudioStretchBuffer::GetFreeSpace() const {
    size_t available = Available();
    return m_capacity - available - 1; // 保留一个样本避免完全填满
}

void OboeAudioRenderer::AudioStretchBuffer::Clear() {
    m_read_pos.store(0, std::memory_order_release);
    m_write_pos.store(0, std::memory_order_release);
    m_stretch_active = false;
    m_current_stretch = 1.0f;
    m_stretch_position = 0;
    std::fill(m_stretch_history.begin(), m_stretch_history.end(), 0);
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::StretchAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::StretchErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamError(error);
}

void OboeAudioRenderer::StretchErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamError(error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<StretchAudioCallback>(this);
    m_error_callback = std::make_unique<StretchErrorCallback>(this);
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
        m_current_mode = "Stretch-Mode (6-channel)";
    } else {
        m_current_mode = "Stable Mode";
    }
    
    // 根据模式计算缓冲区大小
    size_t buffer_duration_ms = (channelCount == 6) ? BUFFER_DURATION_STRETCH_MS : BUFFER_DURATION_STABLE_MS;
    size_t buffer_capacity = (sampleRate * buffer_duration_ms) / 1000;
    m_stretch_buffer = std::make_unique<AudioStretchBuffer>(buffer_capacity, channelCount);
    
    // 6声道启用音频拉伸
    if (channelCount == 6) {
        m_stretch_buffer->SetStretchEnabled(true);
    }
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_stretch_buffer) {
        m_stretch_buffer->Clear();
        m_stretch_buffer.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
}

void OboeAudioRenderer::ConfigureForStretchMode(oboe::AudioStreamBuilder& builder) {
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
        // 6声道: 拉伸模式 - 独占模式，优化的回调大小
        builder.setSharingMode(oboe::SharingMode::Exclusive)
               ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK_STRETCH);
    } else {
        // 其他声道: 稳定模式 - 共享模式，较大的回调
        builder.setSharingMode(oboe::SharingMode::Shared)
               ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK_STABLE);
    }
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置拉伸模式参数
    ConfigureForStretchMode(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 始终使用AAudio
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        return false;
    }
    
    // 根据模式设置缓冲区大小
    int32_t channelCount = m_channel_count.load();
    int32_t target_frames_per_callback = (channelCount == 6) ? 
        TARGET_FRAMES_PER_CALLBACK_STRETCH : TARGET_FRAMES_PER_CALLBACK_STABLE;
    
    int32_t desired_buffer_size = target_frames_per_callback * 4;
    m_stream->setBufferSizeInFrames(desired_buffer_size);
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    
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

float OboeAudioRenderer::CalculateStretchFactor(size_t available_frames, size_t requested_frames) {
    if (available_frames >= requested_frames) {
        return MIN_STRETCH_FACTOR; // 正常速度
    }
    
    if (available_frames == 0) {
        return MAX_STRETCH_FACTOR; // 最大拉伸
    }
    
    // 根据可用帧数计算拉伸系数
    float ratio = static_cast<float>(available_frames) / requested_frames;
    float stretch = MAX_STRETCH_FACTOR + (1.0f - MAX_STRETCH_FACTOR) * ratio;
    
    return std::max(MAX_STRETCH_FACTOR, std::min(MIN_STRETCH_FACTOR, stretch));
}

void OboeAudioRenderer::UpdateStretchState() {
    // 更新拉伸统计
    if (m_stretch_buffer && m_stretch_buffer->IsStretchActive()) {
        m_stretch_activations++;
    }
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        m_write_failures++;
        return false;
    }
    
    if (!m_stretch_buffer) {
        m_write_failures++;
        return false;
    }
    
    // 检查是否有足够空间
    if (m_stretch_buffer->GetFreeSpace() < static_cast<size_t>(num_frames)) {
        m_buffer_overflows++;
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
        success = m_stretch_buffer->Write(volume_adjusted.data(), num_frames);
    } else {
        // 直接写入，无音量调整
        success = m_stretch_buffer->Write(data, num_frames);
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_write_failures++;
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return m_stretch_buffer ? static_cast<int32_t>(m_stretch_buffer->Available()) : 0;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_stretch_buffer) {
        m_stretch_buffer->Clear();
    }
    
    // 重新配置和打开流
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_stretch_buffer) {
        // 输出静音
        int32_t channels = m_channel_count.load();
        std::memset(audioData, 0, num_frames * channels * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }
    
    int16_t* output = static_cast<int16_t*>(audioData);
    int32_t channels = m_channel_count.load();
    
    size_t available_frames = m_stretch_buffer->Available();
    
    if (channels == 6 && m_stretch_buffer->IsStretchActive()) {
        // 6声道模式使用音频拉伸
        float stretch_factor = CalculateStretchFactor(available_frames, num_frames);
        size_t frames_read = m_stretch_buffer->ReadWithStretch(output, num_frames, stretch_factor);
        
        UpdateStretchState();
    } else {
        // 其他声道或数据充足时正常读取
        size_t frames_read = m_stretch_buffer->Read(output, num_frames);
        
        // 如果数据不足，填充静音
        if (frames_read < static_cast<size_t>(num_frames)) {
            size_t samples_remaining = (num_frames - frames_read) * channels;
            std::memset(output + (frames_read * channels), 0, samples_remaining * sizeof(int16_t));
        }
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::Result error) {
    // 在拉伸模式下，我们让上层逻辑决定何时重置
}

OboeAudioRenderer::PerformanceStats OboeAudioRenderer::GetStats() const {
    PerformanceStats stats;
    stats.frames_written = m_frames_written.load();
    stats.frames_played = m_frames_played.load();
    stats.write_failures = m_write_failures.load();
    stats.stream_restart_count = m_stream_restart_count.load();
    stats.buffer_overflows = m_buffer_overflows.load();
    stats.stretch_activations = m_stretch_activations.load();
    stats.mode = m_current_mode;
    return stats;
}

} // namespace RyujinxOboe