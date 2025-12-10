#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat) {
    if (m_initialized.load()) {
        if (m_sample_rate.load() != sampleRate || 
            m_channel_count.load() != channelCount ||
            m_sample_format.load() != sampleFormat) {
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    m_sample_format.store(sampleFormat);
    m_oboe_format = MapSampleFormat(sampleFormat);
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    m_audio_queue.clear();
    m_buffered_frames.store(0);
    m_current_block.reset();
    m_initialized.store(false);
    m_stream_started.store(false);
    
    // 清空效果器状态
    ClearBiquadFilters();
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(240);
    
    auto channel_count = m_channel_count.load();
    auto channel_mask = [&]() {
        switch (channel_count) {
        case 1: return oboe::ChannelMask::Mono;
        case 2: return oboe::ChannelMask::Stereo;
        case 6: return oboe::ChannelMask::CM5Point1;
        default: return oboe::ChannelMask::Unspecified;
        }
    }();
    
    builder.setChannelCount(channel_count)
           ->setChannelMask(channel_mask)
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    ConfigureForAAudioExclusive(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            builder.setAudioApi(oboe::AudioApi::OpenSLES)
                   ->setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
            
            if (result != oboe::Result::OK) {
                return false;
            }
        }
    }
    
    if (!OptimizeBufferSize()) {
        CloseStream();
        return false;
    }
    
    m_device_channels = m_stream->getChannelCount();
    
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) return false;
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    int32_t desired_buffer_size = framesPerBurst > 0 ? framesPerBurst * 2 : 960;
    
    m_stream->setBufferSizeInFrames(desired_buffer_size);
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
    if (!m_initialized.load() || !data || num_frames <= 0) return false;
    
    int32_t system_channels = m_channel_count.load();
    size_t data_size = num_frames * system_channels * sizeof(int16_t);
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat) {
    if (!m_initialized.load() || !data || num_frames <= 0) return false;
    
    // 检查格式是否匹配
    if (sampleFormat != m_sample_format.load()) {
        return false;
    }
    
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t frame_size = system_channels * bytes_per_sample;
    size_t total_bytes = num_frames * frame_size;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    int32_t total_frames_written = 0;
    
    while (bytes_remaining > 0) {
        auto block = m_object_pool.acquire();
        if (!block) {
            // 如果获取块失败，减去已写入的帧数
            m_buffered_frames.fetch_sub(total_frames_written);
            return false;
        }
        
        // 确保数据大小是帧大小的整数倍
        size_t max_copy = (AudioBlock::BLOCK_SIZE / frame_size) * frame_size;
        size_t copy_size = std::min(bytes_remaining, max_copy);
        
        if (copy_size == 0) {
            m_object_pool.release(std::move(block));
            m_buffered_frames.fetch_sub(total_frames_written);
            return false;
        }
        
        int32_t frames_in_block = static_cast<int32_t>(copy_size / frame_size);
        total_frames_written += frames_in_block;
        
        std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) {
            m_buffered_frames.fetch_sub(total_frames_written);
            return false;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
    }
    
    // 更新缓冲帧数
    m_buffered_frames.fetch_add(total_frames_written);
    return true;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return static_cast<int32_t>(m_buffered_frames.load());
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_audio_queue.clear();
    m_buffered_frames.store(0);
    
    if (m_current_block) {
        m_object_pool.release(std::move(m_current_block));
    }
    
    CloseStream();
    ConfigureAndOpenStream();
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        default:         return oboe::AudioFormat::I16;
    }
}

size_t OboeAudioRenderer::GetBytesPerSample(int32_t format) {
    switch (format) {
        case PCM_INT16:  return 2;
        case PCM_INT24:  return 3;
        case PCM_INT32:  return 4;
        case PCM_FLOAT:  return 4;
        default:         return 2;
    }
}

bool OboeAudioRenderer::SetBiquadFilterParameters(const uint8_t* param_data) {
    if (!param_data) return false;
    
    std::lock_guard<std::mutex> lock(m_filter_mutex);
    
    // 解析参数，跳过padding字段
    // Input (6 bytes) - 偏移 0
    // Output (6 bytes) - 偏移 6
    // padding (4 bytes) - 偏移 12，跳过
    // Numerator (12 bytes) - 偏移 16
    // Denominator (8 bytes) - 偏移 28
    // ChannelCount (1 byte) - 偏移 36
    // Status (1 byte) - 偏移 37
    // reserved (2 bytes) - 偏移 38
    
    const float* numerator = reinterpret_cast<const float*>(param_data + 16);
    const float* denominator = reinterpret_cast<const float*>(param_data + 28);
    uint8_t channelCount = param_data[36];
    uint8_t status = param_data[37];
    
    // 验证参数
    if (channelCount > 6 || channelCount == 0) {
        return false;
    }
    
    // 创建或更新滤波器状态
    if (m_biquad_filters.empty()) {
        m_biquad_filters.resize(1);
    }
    
    auto& filter = m_biquad_filters[0];
    filter.b0 = numerator[0];
    filter.b1 = numerator[1];
    filter.b2 = numerator[2];
    filter.a1 = denominator[0];
    filter.a2 = denominator[1];
    filter.channelCount = channelCount;
    filter.enabled = (status == 1);  // 假设1表示启用
    
    // 当滤波器参数变化时，重置历史状态以避免瞬态噪声
    filter.reset();
    
    // 更新全局启用状态
    m_biquad_enabled = filter.enabled;
    
    return true;
}

void OboeAudioRenderer::EnableBiquadFilter(bool enable) {
    std::lock_guard<std::mutex> lock(m_filter_mutex);
    
    m_biquad_enabled = enable;
    
    if (!m_biquad_filters.empty()) {
        m_biquad_filters[0].enabled = enable;
        // 重置滤波器历史状态
        m_biquad_filters[0].reset();
    }
}

void OboeAudioRenderer::ClearBiquadFilters() {
    std::lock_guard<std::mutex> lock(m_filter_mutex);
    m_biquad_filters.clear();
    m_biquad_enabled = false;
}

void OboeAudioRenderer::ApplyBiquadFilterInt16(int16_t* audio_data, int32_t num_frames, int32_t channels) {
    if (!m_biquad_enabled || m_biquad_filters.empty()) return;
    
    std::lock_guard<std::mutex> lock(m_filter_mutex);
    
    for (auto& filter : m_biquad_filters) {
        if (!filter.enabled || filter.channelCount != channels) continue;
        
        // 应用二阶IIR滤波器到int16音频数据
        // 直接形式II: y[n] = b0*x[n] + b1*x[n-1] + b2*x[n-2] - a1*y[n-1] - a2*y[n-2]
        for (int32_t frame = 0; frame < num_frames; frame++) {
            for (int32_t ch = 0; ch < channels; ch++) {
                int32_t idx = frame * channels + ch;
                float x = static_cast<float>(audio_data[idx]) / 32768.0f;  // 转换为[-1, 1]范围
                
                // 计算输出
                float y = filter.b0 * x + filter.b1 * filter.x1[ch] + filter.b2 * filter.x2[ch]
                        - filter.a1 * filter.y1[ch] - filter.a2 * filter.y2[ch];
                
                // 限制输出范围，防止溢出
                y = std::max(-1.0f, std::min(1.0f, y));
                
                // 更新历史状态
                filter.x2[ch] = filter.x1[ch];
                filter.x1[ch] = x;
                filter.y2[ch] = filter.y1[ch];
                filter.y1[ch] = y;
                
                // 转换回int16
                audio_data[idx] = static_cast<int16_t>(y * 32767.0f);
            }
        }
    }
}

void OboeAudioRenderer::ApplyBiquadFilterFloat(float* audio_data, int32_t num_frames, int32_t channels) {
    if (!m_biquad_enabled || m_biquad_filters.empty()) return;
    
    std::lock_guard<std::mutex> lock(m_filter_mutex);
    
    for (auto& filter : m_biquad_filters) {
        if (!filter.enabled || filter.channelCount != channels) continue;
        
        // 应用二阶IIR滤波器到float音频数据
        for (int32_t frame = 0; frame < num_frames; frame++) {
            for (int32_t ch = 0; ch < channels; ch++) {
                int32_t idx = frame * channels + ch;
                float x = audio_data[idx];
                
                // 计算输出
                float y = filter.b0 * x + filter.b1 * filter.x1[ch] + filter.b2 * filter.x2[ch]
                        - filter.a1 * filter.y1[ch] - filter.a2 * filter.y2[ch];
                
                // 限制输出范围，防止溢出
                y = std::max(-1.0f, std::min(1.0f, y));
                
                // 更新历史状态
                filter.x2[ch] = filter.x1[ch];
                filter.x1[ch] = x;
                filter.y2[ch] = filter.y1[ch];
                filter.y1[ch] = y;
                
                audio_data[idx] = y;
            }
        }
    }
}

void OboeAudioRenderer::ApplyBiquadFilterInt32(int32_t* audio_data, int32_t num_frames, int32_t channels) {
    if (!m_biquad_enabled || m_biquad_filters.empty()) return;
    
    std::lock_guard<std::mutex> lock(m_filter_mutex);
    
    for (auto& filter : m_biquad_filters) {
        if (!filter.enabled || filter.channelCount != channels) continue;
        
        // 应用二阶IIR滤波器到int32音频数据
        const float scale = 1.0f / 2147483648.0f;  // 转换为[-1, 1]范围
        const float inv_scale = 2147483648.0f;     // 转换回int32
        
        for (int32_t frame = 0; frame < num_frames; frame++) {
            for (int32_t ch = 0; ch < channels; ch++) {
                int32_t idx = frame * channels + ch;
                float x = static_cast<float>(audio_data[idx]) * scale;
                
                // 计算输出
                float y = filter.b0 * x + filter.b1 * filter.x1[ch] + filter.b2 * filter.x2[ch]
                        - filter.a1 * filter.y1[ch] - filter.a2 * filter.y2[ch];
                
                // 限制输出范围，防止溢出
                y = std::max(-1.0f, std::min(1.0f, y));
                
                // 更新历史状态
                filter.x2[ch] = filter.x1[ch];
                filter.x1[ch] = x;
                filter.y2[ch] = filter.y1[ch];
                filter.y1[ch] = y;
                
                // 转换回int32
                audio_data[idx] = static_cast<int32_t>(y * inv_scale);
            }
        }
    }
}

void OboeAudioRenderer::ApplyVolumeInt16(int16_t* audio_data, int32_t num_frames, int32_t channels, float volume) {
    if (volume >= 0.999f && volume <= 1.001f) return;
    
    int32_t total_samples = num_frames * channels;
    for (int32_t i = 0; i < total_samples; i++) {
        float sample_f = static_cast<float>(audio_data[i]) * volume;
        // 限制在int16范围内
        if (sample_f > 32767.0f) sample_f = 32767.0f;
        if (sample_f < -32768.0f) sample_f = -32768.0f;
        audio_data[i] = static_cast<int16_t>(sample_f);
    }
}

void OboeAudioRenderer::ApplyVolumeFloat(float* audio_data, int32_t num_frames, int32_t channels, float volume) {
    if (volume >= 0.999f && volume <= 1.001f) return;
    
    int32_t total_samples = num_frames * channels;
    for (int32_t i = 0; i < total_samples; i++) {
        audio_data[i] *= volume;
        // 限制在[-1.0, 1.0]范围内
        if (audio_data[i] > 1.0f) audio_data[i] = 1.0f;
        if (audio_data[i] < -1.0f) audio_data[i] = -1.0f;
    }
}

void OboeAudioRenderer::ApplyVolumeInt32(int32_t* audio_data, int32_t num_frames, int32_t channels, float volume) {
    if (volume >= 0.999f && volume <= 1.001f) return;
    
    int32_t total_samples = num_frames * channels;
    for (int32_t i = 0; i < total_samples; i++) {
        int64_t sample64 = static_cast<int64_t>(audio_data[i]) * static_cast<int64_t>(volume * 65536.0f);
        sample64 >>= 16; // 相当于除以65536
        if (sample64 > 2147483647) sample64 = 2147483647;
        if (sample64 < -2147483648) sample64 = -2147483648;
        audio_data[i] = static_cast<int32_t>(sample64);
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    // 关键修复：简化回调逻辑，确保数据连续性
    
    if (!m_initialized.load() || !audioStream || !audioData) {
        return oboe::DataCallbackResult::Continue;
    }
    
    // 检查流状态
    if (!m_stream || m_stream->getState() != oboe::StreamState::Started) {
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t device_channels = m_stream->getChannelCount();
    int32_t sample_format = m_sample_format.load();
    size_t bytes_per_sample = GetBytesPerSample(sample_format);
    
    // 清空输出缓冲区
    size_t total_bytes = static_cast<size_t>(num_frames) * device_channels * bytes_per_sample;
    std::memset(audioData, 0, total_bytes);
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_copied = 0;
    size_t frames_copied = 0;
    
    // 持续处理直到满足请求的帧数
    while (frames_copied < static_cast<size_t>(num_frames)) {
        // 获取当前块
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            // 如果有旧块，释放它
            if (m_current_block) {
                // 计算这个块中未播放的帧数并减去
                if (m_current_block->data_size > m_current_block->data_played) {
                    size_t bytes_remaining = m_current_block->available();
                    int32_t system_channels = m_channel_count.load();
                    size_t frame_size = system_channels * bytes_per_sample;
                    int32_t frames_remaining = frame_size > 0 ? 
                        static_cast<int32_t>(bytes_remaining / frame_size) : 0;
                    m_buffered_frames.fetch_sub(frames_remaining);
                }
                
                m_object_pool.release(std::move(m_current_block));
                m_current_block.reset();
            }
            
            // 尝试从队列获取新块
            if (!m_audio_queue.pop(m_current_block)) {
                // 没有更多数据，跳出循环
                break;
            }
        }
        
        // 检查格式匹配
        if (m_current_block->sample_format != sample_format) {
            // 格式不匹配，丢弃这个块并减去对应的帧数
            size_t bytes_remaining = m_current_block->available();
            int32_t system_channels = m_channel_count.load();
            size_t frame_size = system_channels * bytes_per_sample;
            int32_t frames_remaining = frame_size > 0 ? 
                static_cast<int32_t>(bytes_remaining / frame_size) : 0;
            m_buffered_frames.fetch_sub(frames_remaining);
            
            m_object_pool.release(std::move(m_current_block));
            m_current_block.reset();
            continue;
        }
        
        // 计算这个块中还有多少可用数据
        size_t block_available = m_current_block->available();
        if (block_available == 0) {
            m_current_block->consumed = true;
            continue;
        }
        
        // 计算还需要多少数据
        size_t remaining_bytes = total_bytes - bytes_copied;
        size_t bytes_to_copy = std::min(block_available, remaining_bytes);
        
        if (bytes_to_copy == 0) {
            break;
        }
        
        // 直接从当前块拷贝数据到输出
        std::memcpy(output + bytes_copied,
                   m_current_block->data + m_current_block->data_played,
                   bytes_to_copy);
        
        // 更新计数器
        bytes_copied += bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        // 重新计算已拷贝的帧数
        frames_copied = bytes_copied / (device_channels * bytes_per_sample);
        
        // 如果当前块已用完，标记为已消费
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    // 更新缓冲帧数
    m_buffered_frames.fetch_sub(static_cast<int32_t>(frames_copied));
    
    // 应用音频效果器（如果需要）
    float current_volume = m_volume.load();
    
    switch (sample_format) {
        case PCM_INT16:
            if (current_volume < 0.999f || current_volume > 1.001f) {
                ApplyVolumeInt16(static_cast<int16_t*>(audioData), static_cast<int32_t>(frames_copied), device_channels, current_volume);
            }
            ApplyBiquadFilterInt16(static_cast<int16_t*>(audioData), static_cast<int32_t>(frames_copied), device_channels);
            break;
            
        case PCM_FLOAT:
            if (current_volume < 0.999f || current_volume > 1.001f) {
                ApplyVolumeFloat(static_cast<float*>(audioData), static_cast<int32_t>(frames_copied), device_channels, current_volume);
            }
            ApplyBiquadFilterFloat(static_cast<float*>(audioData), static_cast<int32_t>(frames_copied), device_channels);
            break;
            
        case PCM_INT32:
            if (current_volume < 0.999f || current_volume > 1.001f) {
                ApplyVolumeInt32(static_cast<int32_t*>(audioData), static_cast<int32_t>(frames_copied), device_channels, current_volume);
            }
            ApplyBiquadFilterInt32(static_cast<int32_t*>(audioData), static_cast<int32_t>(frames_copied), device_channels);
            break;
            
        case PCM_INT24:
            // PCM_INT24 格式暂不支持效果器，只应用音量控制
            if (current_volume < 0.999f || current_volume > 1.001f) {
                // 需要特殊的int24音量控制实现
            }
            break;
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_initialized.load()) {
        CloseStream();
        
        // 清空音频队列并重置计数器
        m_audio_queue.clear();
        m_buffered_frames.store(0);
        
        if (m_current_block) {
            m_object_pool.release(std::move(m_current_block));
        }
        
        // 延迟重启以避免频繁重试
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        
        ConfigureAndOpenStream();
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false);
}

oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    return m_renderer->OnAudioReadyMultiFormat(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe