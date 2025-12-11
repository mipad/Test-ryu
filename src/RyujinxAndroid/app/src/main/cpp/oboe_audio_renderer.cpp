#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
    
    // 初始化对象池
    m_block_pool.reserve(BLOCK_POOL_SIZE);
    for (size_t i = 0; i < BLOCK_POOL_SIZE; ++i) {
        m_block_pool.push_back(std::make_unique<AudioBlock>());
    }
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

std::unique_ptr<AudioBlock> OboeAudioRenderer::create_audio_block() {
    size_t current_used = m_block_pool_used.load();
    
    if (current_used < m_block_pool.size()) {
        if (m_block_pool_used.compare_exchange_weak(current_used, current_used + 1)) {
            auto block = std::move(m_block_pool[current_used]);
            return block;
        }
    }
    
    // 池耗尽，动态创建
    return std::make_unique<AudioBlock>();
}

void OboeAudioRenderer::return_audio_block(std::unique_ptr<AudioBlock> block) {
    if (!block) return;
    
    block->clear();
    
    size_t current_used = m_block_pool_used.load();
    if (current_used > 0) {
        // 尝试放回池中
        size_t new_used = current_used - 1;
        if (m_block_pool_used.compare_exchange_weak(current_used, new_used)) {
            m_block_pool[new_used] = std::move(block);
            return;
        }
    }
    // 如果池已满或CAS失败，block将被自动释放
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
    
    if (!TryOpenStreamWithRetry(3)) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    m_audio_queue.clear();
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

bool OboeAudioRenderer::TryOpenStreamWithRetry(int maxRetryCount) {
    for (int attempt = 0; attempt < maxRetryCount; ++attempt) {
        if (ConfigureAndOpenStream()) {
            return true;
        }
        
        if (attempt < maxRetryCount - 1) {
            std::this_thread::sleep_for(std::chrono::milliseconds(50 * (1 << attempt)));
        }
    }
    return false;
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
    
    int32_t desired_buffer_size;
    
    if (framesPerBurst > 0) {
        // 使用脉冲帧数的2倍
        desired_buffer_size = framesPerBurst * 2;
        
        // 确保至少有一个合理的缓冲区（10ms）
        int32_t sample_rate = m_sample_rate.load();
        int32_t min_frames = sample_rate / 100;  // 10ms
        
        if (desired_buffer_size < min_frames) {
            desired_buffer_size = min_frames;
            
            // 确保是脉冲帧数的整数倍
            if (desired_buffer_size % framesPerBurst != 0) {
                desired_buffer_size = ((desired_buffer_size / framesPerBurst) + 1) * framesPerBurst;
            }
        }
    } else {
        // 基于采样率计算回退值
        int32_t sample_rate = m_sample_rate.load();
        
        // 目标20ms延迟
        desired_buffer_size = sample_rate / 50;
        
        // 确保在合理范围内
        int32_t max_frames = sample_rate / 10;    // 100ms最大
        int32_t min_frames = sample_rate / 200;   // 5ms最小
        
        if (desired_buffer_size < min_frames) {
            desired_buffer_size = min_frames;
        } else if (desired_buffer_size > max_frames) {
            desired_buffer_size = max_frames;
        }
    }
    
    // 确保缓冲区大小是2的倍数（一些硬件要求）
    desired_buffer_size = (desired_buffer_size + 1) & ~1;
    
    auto result = m_stream->setBufferSizeInFrames(desired_buffer_size);
    
    return result == oboe::Result::OK;
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
    
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t total_bytes = num_frames * system_channels * bytes_per_sample;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    while (bytes_remaining > 0) {
        auto block = create_audio_block();
        if (!block) {
            // 无法创建块，可能是内存不足
            return false;
        }
        
        // 确保块有足够容量
        size_t copy_size = std::min(bytes_remaining, AudioBlock::DEFAULT_BLOCK_SIZE);
        block->ensure_capacity(copy_size);
        
        if (sampleFormat == PCM_INT16) {
            const int16_t* src = reinterpret_cast<const int16_t*>(byte_data + bytes_processed);
            int16_t* dst = reinterpret_cast<int16_t*>(block->data.data());
            std::memcpy(dst, src, copy_size);
        } else if (sampleFormat == PCM_FLOAT) {
            const float* src = reinterpret_cast<const float*>(byte_data + bytes_processed);
            float* dst = reinterpret_cast<float*>(block->data.data());
            std::memcpy(dst, src, copy_size);
        } else {
            std::memcpy(block->data.data(), byte_data + bytes_processed, copy_size);
        }
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        // 使用动态队列推送，永不阻塞
        if (!m_audio_queue.push(std::move(block))) {
            // 队列推送失败（即使动态队列也可能失败，如内存不足）
            return_audio_block(std::move(block));
            return false;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
    }
    
    return true;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load()) return 0;
    
    int32_t total_frames = 0;
    int32_t device_channels = m_device_channels;
    
    if (m_current_block && !m_current_block->consumed) {
        size_t bytes_remaining = m_current_block->available();
        size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        total_frames += static_cast<int32_t>(bytes_remaining / (device_channels * bytes_per_sample));
    }
    
    // 估算队列中的帧数
    size_t queue_size = m_audio_queue.size();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    
    if (device_channels > 0 && bytes_per_sample > 0) {
        // 假设每个块平均填充了一半
        size_t avg_bytes_per_block = AudioBlock::DEFAULT_BLOCK_SIZE / 2;
        size_t avg_frames_per_block = avg_bytes_per_block / (device_channels * bytes_per_sample);
        total_frames += static_cast<int32_t>(queue_size * avg_frames_per_block);
    }
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_audio_queue.clear();
    if (m_current_block) {
        return_audio_block(std::move(m_current_block));
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
    if (!m_initialized.load()) {
        int32_t channels = m_device_channels;
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        size_t bytes_requested = num_frames * channels * bytes_per_sample;
        std::memset(audioData, 0, bytes_requested);
        return oboe::DataCallbackResult::Continue;
    }
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_remaining = num_frames * m_device_channels * GetBytesPerSample(m_sample_format.load());
    size_t bytes_copied = 0;
    
    while (bytes_remaining > 0) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                return_audio_block(std::move(m_current_block));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 队列为空，输出静音
                std::memset(output + bytes_copied, 0, bytes_remaining);
                break;
            }
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_remaining);
        std::memcpy(output + bytes_copied, 
                   m_current_block->data.data() + m_current_block->data_played,
                   bytes_to_copy);
        
        bytes_copied += bytes_to_copy;
        bytes_remaining -= bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_initialized.load()) {
        CloseStream();
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
