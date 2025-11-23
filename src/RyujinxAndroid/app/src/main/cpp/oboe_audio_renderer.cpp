#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <android/log.h>

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
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    m_sample_format.store(sampleFormat);
    m_oboe_format = MapSampleFormat(sampleFormat);
    
    // 清空队列
    m_audio_queue.clear();
    m_current_block.reset();
    
    if (!TryOpenStreamWithRetry(3)) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
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
           ->setFramesPerCallback(0); // 让Oboe自动选择
    
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
            std::this_thread::sleep_for(std::chrono::milliseconds(20 * (1 << attempt)));
            CloseStream();
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
        // 尝试共享模式
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            return false;
        }
    }
    
    // 获取实际的burst大小
    m_frames_per_burst = m_stream->getFramesPerBurst();
    if (m_frames_per_burst <= 0) {
        m_frames_per_burst = 256; // 默认值
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
    
    // 根据burst大小自动调整缓冲区
    int32_t desired_buffer_size = m_frames_per_burst * 2; // 2倍burst大小
    
    auto result = m_stream->setBufferSizeInFrames(desired_buffer_size);
    if (result != oboe::Result::OK) {
        // 如果设置失败，使用默认值
        desired_buffer_size = m_frames_per_burst;
        m_stream->setBufferSizeInFrames(desired_buffer_size);
    }
    
    return true;
}

size_t OboeAudioRenderer::CalculateOptimalBlockSize() const {
    // 根据burst大小和声道数计算最佳块大小
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t frames_per_block = m_frames_per_burst * 2; // 2倍burst
    return frames_per_block * m_device_channels * bytes_per_sample;
}

std::unique_ptr<AudioBlock> OboeAudioRenderer::CreateAudioBlock(size_t size) {
    return std::make_unique<AudioBlock>(size);
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
        // 动态创建适当大小的音频块
        size_t optimal_size = CalculateOptimalBlockSize();
        auto block = CreateAudioBlock(optimal_size);
        if (!block || !block->data) {
            return false;
        }
        
        size_t copy_size = std::min(bytes_remaining, block->data_size);
        
        // 复制数据到音频块
        std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        block->data_used = copy_size;
        block->sample_format = sampleFormat;
        
        // 将块推入无锁队列
        if (!m_audio_queue.push(std::move(block))) {
            // 队列已满，丢弃这个块
            break;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
    }
    
    // 返回实际写入的字节数是否等于总字节数
    return bytes_processed == total_bytes;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load()) return 0;
    
    int32_t total_frames = 0;
    int32_t device_channels = m_device_channels;
    
    // 计算当前块中剩余的数据
    if (m_current_block) {
        size_t bytes_remaining = m_current_block->data_used;
        size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        if (bytes_per_sample > 0 && device_channels > 0) {
            total_frames += static_cast<int32_t>(bytes_remaining / (device_channels * bytes_per_sample));
        }
    }
    
    // 计算队列中所有块的数据
    // 注意：我们无法精确计算，所以使用估计值
    uint32_t queue_size = m_audio_queue.size();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    if (bytes_per_sample > 0 && device_channels > 0) {
        size_t optimal_block_size = CalculateOptimalBlockSize();
        int32_t frames_per_block = static_cast<int32_t>(optimal_block_size / (device_channels * bytes_per_sample));
        total_frames += queue_size * frames_per_block;
    }
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::Reset() {
    m_audio_queue.clear();
    m_current_block.reset();
    
    CloseStream();
    
    // 重新初始化流
    if (m_initialized.load()) {
        ConfigureAndOpenStream();
    }
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
        // 如果没有当前块或者当前块已用完，从队列获取新块
        if (!m_current_block || m_current_block->data_used == 0) {
            m_current_block.reset();
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 队列为空，用静音填充剩余部分
                std::memset(output + bytes_copied, 0, bytes_remaining);
                break;
            }
        }
        
        // 从当前块复制数据
        size_t bytes_available = m_current_block->data_used;
        size_t bytes_to_copy = std::min(bytes_available, bytes_remaining);
        
        std::memcpy(output + bytes_copied, 
                   m_current_block->data,
                   bytes_to_copy);
        
        bytes_copied += bytes_to_copy;
        bytes_remaining -= bytes_to_copy;
        
        // 更新当前块的使用情况
        if (bytes_to_copy == m_current_block->data_used) {
            // 整个块已用完
            m_current_block.reset();
        } else {
            // 部分块已用完，移动剩余数据到块的开头
            size_t remaining_bytes = m_current_block->data_used - bytes_to_copy;
            std::memmove(m_current_block->data, 
                        m_current_block->data + bytes_to_copy,
                        remaining_bytes);
            m_current_block->data_used = remaining_bytes;
        }
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    if (m_initialized.load()) {
        CloseStream();
        ConfigureAndOpenStream();
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
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