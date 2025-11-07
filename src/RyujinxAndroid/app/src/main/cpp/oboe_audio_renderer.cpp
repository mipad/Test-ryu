// oboe_audio_renderer.cpp (实时音频版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

// =============== RealTimeAudioBuffer Implementation ===============
OboeAudioRenderer::RealTimeAudioBuffer::RealTimeAudioBuffer(size_t frame_capacity, int32_t channels) 
    : m_total_frames(0),
      m_frame_capacity(frame_capacity),
      m_channels(channels),
      m_low_latency_mode(true),
      m_frames_written(0),
      m_frames_read(0),
      m_usage_ratio(0.5) {
}

OboeAudioRenderer::RealTimeAudioBuffer::~RealTimeAudioBuffer() {
    Clear();
}

bool OboeAudioRenderer::RealTimeAudioBuffer::Write(const int16_t* data, size_t frames) {
    if (!data || frames == 0) return false;
    
    std::lock_guard<std::mutex> lock(m_queue_mutex);
    
    // 实时模式：如果缓冲区快满了，丢弃旧数据为新数据腾出空间
    if (m_low_latency_mode && m_total_frames + frames > m_frame_capacity) {
        while (!m_chunks.empty() && m_total_frames + frames > m_frame_capacity) {
            m_total_frames -= m_chunks.front().frames;
            m_chunks.pop();
        }
    }
    
    // 检查空间
    if (m_total_frames + frames > m_frame_capacity) {
        return false;
    }
    
    // 创建新的音频块
    AudioChunk chunk;
    chunk.frames = frames;
    chunk.data.resize(frames * m_channels);
    std::memcpy(chunk.data.data(), data, frames * m_channels * sizeof(int16_t));
    
    m_chunks.push(std::move(chunk));
    m_total_frames += frames;
    m_frames_written += frames;
    
    // 更新使用率统计
    m_usage_ratio = static_cast<double>(m_total_frames) / m_frame_capacity;
    
    return true;
}

size_t OboeAudioRenderer::RealTimeAudioBuffer::Read(int16_t* output, size_t frames) {
    if (!output || frames == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_queue_mutex);
    
    if (m_chunks.empty()) {
        return 0;
    }
    
    size_t frames_read = 0;
    size_t samples_copied = 0;
    
    while (frames_read < frames && !m_chunks.empty()) {
        AudioChunk& chunk = m_chunks.front();
        size_t frames_to_read = std::min(chunk.frames, frames - frames_read);
        size_t samples_to_copy = frames_to_read * m_channels;
        
        std::memcpy(output + samples_copied, chunk.data.data(), samples_to_copy * sizeof(int16_t));
        
        frames_read += frames_to_read;
        samples_copied += samples_to_copy;
        
        if (frames_to_read == chunk.frames) {
            // 整个块已读取，移除
            m_total_frames -= chunk.frames;
            m_chunks.pop();
        } else {
            // 部分读取，更新块
            size_t remaining_frames = chunk.frames - frames_to_read;
            std::vector<int16_t> remaining_data(remaining_frames * m_channels);
            std::memcpy(remaining_data.data(), chunk.data.data() + samples_to_copy, remaining_frames * m_channels * sizeof(int16_t));
            
            chunk.frames = remaining_frames;
            chunk.data = std::move(remaining_data);
        }
    }
    
    m_frames_read += frames_read;
    
    // 更新使用率统计
    m_usage_ratio = static_cast<double>(m_total_frames) / m_frame_capacity;
    
    return frames_read;
}

size_t OboeAudioRenderer::RealTimeAudioBuffer::Available() const {
    std::lock_guard<std::mutex> lock(m_queue_mutex);
    return m_total_frames;
}

size_t OboeAudioRenderer::RealTimeAudioBuffer::GetFreeSpace() const {
    std::lock_guard<std::mutex> lock(m_queue_mutex);
    return m_frame_capacity - m_total_frames;
}

void OboeAudioRenderer::RealTimeAudioBuffer::Clear() {
    std::lock_guard<std::mutex> lock(m_queue_mutex);
    while (!m_chunks.empty()) {
        m_chunks.pop();
    }
    m_total_frames = 0;
    m_usage_ratio = 0.0;
}

void OboeAudioRenderer::RealTimeAudioBuffer::SetLowLatencyMode(bool enabled) {
    m_low_latency_mode = enabled;
}

void OboeAudioRenderer::RealTimeAudioBuffer::AdjustBufferBasedOnUsage() {
    // 根据使用率动态调整缓冲区行为
    if (m_usage_ratio > 0.8) {
        // 高使用率，启用低延迟模式
        m_low_latency_mode = true;
    } else if (m_usage_ratio < 0.3) {
        // 低使用率，可以稍微放松
        m_low_latency_mode = false;
    }
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::RealTimeAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::RealTimeErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamError(error);
}

void OboeAudioRenderer::RealTimeErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error: %d", error);
    m_renderer->OnStreamError(error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<RealTimeAudioCallback>(this);
    m_error_callback = std::make_unique<RealTimeErrorCallback>(this);
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
        if (m_sample_rate.load() != sampleRate || m_channel_count.load() != channelCount) {
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    
    // 实时音频：使用动态缓冲区大小
    size_t buffer_duration_ms = m_real_time_mode.load() ? MIN_BUFFER_DURATION_MS : MAX_BUFFER_DURATION_MS;
    size_t buffer_capacity = (sampleRate * buffer_duration_ms) / 1000;
    m_audio_buffer = std::make_unique<RealTimeAudioBuffer>(buffer_capacity, channelCount);
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_audio_buffer) {
        m_audio_buffer->Clear();
        m_audio_buffer.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
}

void OboeAudioRenderer::ConfigureForRealTimeAudio(oboe::AudioStreamBuilder& builder) {
    // 实时音频配置：最低延迟
    builder.setAudioApi(oboe::AudioApi::AAudio)
           ->setPerformanceMode(PERFORMANCE_MODE)
           ->setSharingMode(SHARING_MODE)
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(m_channel_count.load())
           ->setSampleRate(m_sample_rate.load())
           ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::None) // 禁用重采样以获得最低延迟
           ->setFormatConversionAllowed(false)  // 禁用格式转换
           ->setChannelConversionAllowed(false) // 禁用声道转换
           ->setUsage(oboe::Usage::Game)        // 游戏用途，最低延迟
           ->setContentType(oboe::ContentType::Game); // 游戏内容类型
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置实时音频参数
    ConfigureForRealTimeAudio(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 始终使用AAudio
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        return false;
    }
    
    // 实时音频：使用最小缓冲区
    int32_t min_buffer_size = m_stream->getFramesPerBurst();
    auto setBufferResult = m_stream->setBufferSizeInFrames(min_buffer_size * 2);
    
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

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    if (!m_audio_buffer) {
        return false;
    }
    
    // 实时音频：在写入前调整缓冲区行为
    m_audio_buffer->AdjustBufferBasedOnUsage();
    
    // 应用音量
    float volume = m_volume.load();
    bool apply_volume = (volume != 1.0f);
    
    bool success;
    
    if (apply_volume) {
        // 需要应用音量，创建临时缓冲区
        std::vector<int16_t> volume_adjusted(num_frames * m_channel_count.load());
        for (int32_t i = 0; i < num_frames * m_channel_count.load(); i++) {
            volume_adjusted[i] = static_cast<int16_t>(data[i] * volume);
        }
        success = m_audio_buffer->Write(volume_adjusted.data(), num_frames);
    } else {
        // 直接写入，无音量调整
        success = m_audio_buffer->Write(data, num_frames);
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_overrun_count++;
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return m_audio_buffer ? static_cast<int32_t>(m_audio_buffer->Available()) : 0;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_audio_buffer) {
        m_audio_buffer->Clear();
    }
    
    // 重新配置和打开流
    CloseStream();
    ConfigureAndOpenStream();
}

void OboeAudioRenderer::SetRealTimeMode(bool enabled) {
    m_real_time_mode.store(enabled);
    if (m_audio_buffer) {
        m_audio_buffer->SetLowLatencyMode(enabled);
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_audio_buffer) {
        // 输出静音
        int32_t channels = m_channel_count.load();
        std::memset(audioData, 0, num_frames * channels * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }
    
    int16_t* output = static_cast<int16_t*>(audioData);
    int32_t channels = m_channel_count.load();
    
    // 从音频缓冲区读取数据
    size_t frames_read = m_audio_buffer->Read(output, num_frames);
    
    // 实时音频：如果数据不足，智能填充
    if (frames_read < static_cast<size_t>(num_frames)) {
        size_t samples_remaining = (num_frames - frames_read) * channels;
        
        // 使用渐入渐出避免爆音
        if (frames_read > 0) {
            // 渐出：最后几帧逐渐降低音量
            int32_t fade_frames = std::min(static_cast<int32_t>(frames_read), 8);
            for (int32_t i = 0; i < fade_frames; i++) {
                float fade_factor = 1.0f - (static_cast<float>(i) / fade_frames);
                int32_t frame_offset = frames_read - fade_frames + i;
                for (int32_t ch = 0; ch < channels; ch++) {
                    int32_t sample_index = frame_offset * channels + ch;
                    output[sample_index] = static_cast<int16_t>(output[sample_index] * fade_factor);
                }
            }
        }
        
        // 填充静音
        std::memset(output + (frames_read * channels), 0, samples_remaining * sizeof(int16_t));
        
        m_underrun_count++;
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::Result error) {
    // 实时音频：不立即重置，避免中断
}

} // namespace RyujinxOboe