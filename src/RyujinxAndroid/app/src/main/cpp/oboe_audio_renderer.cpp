#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

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

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !audioStream || !audioData) {
        if (audioData && num_frames > 0) {
            size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
            int32_t channels = m_stream ? m_stream->getChannelCount() : m_channel_count.load();
            std::memset(audioData, 0, num_frames * channels * bytes_per_sample);
        }
        return oboe::DataCallbackResult::Continue;
    }
    
    if (m_stream->getState() != oboe::StreamState::Started) {
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
    size_t frames_processed = 0;
    
    while (frames_processed < static_cast<size_t>(num_frames)) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
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
            
            if (!m_audio_queue.pop(m_current_block)) {
                break; // 队列为空
            }
        }
        
        if (m_current_block->sample_format != sample_format) {
            // 格式不匹配，丢弃整个块并减去对应的帧数
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
        
        size_t block_available = m_current_block->available();
        if (block_available == 0) {
            m_current_block->consumed = true;
            continue;
        }
        
        size_t remaining_frames = static_cast<size_t>(num_frames) - frames_processed;
        size_t remaining_bytes = remaining_frames * device_channels * bytes_per_sample - bytes_copied;
        size_t bytes_to_copy = std::min(block_available, remaining_bytes);
        
        if (bytes_to_copy == 0) {
            break;
        }
        
        std::memcpy(output + bytes_copied,
                   m_current_block->data + m_current_block->data_played,
                   bytes_to_copy);
        
        bytes_copied += bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        frames_processed = bytes_copied / (device_channels * bytes_per_sample);
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    // 减去已播放的帧数
    m_buffered_frames.fetch_sub(static_cast<int32_t>(frames_processed));
    
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