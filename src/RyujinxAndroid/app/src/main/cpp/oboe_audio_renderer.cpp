#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() 
    : m_last_adjust_time(std::chrono::steady_clock::now()) {
    m_audio_callback = std::make_unique<SimpleAudioCallback>(this);
    m_error_callback = std::make_unique<SimpleErrorCallback>(this);
    InitializePool(32); // 初始化较小对象池
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

void OboeAudioRenderer::InitializePool(size_t size) {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    m_block_pool.reserve(size);
    
    for (size_t i = 0; i < size; ++i) {
        auto block = std::make_unique<AudioBlock>();
        block->clear();
        m_block_pool.push_back(std::move(block));
    }
}

std::unique_ptr<AudioBlock> OboeAudioRenderer::AcquireBlock() {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    
    if (!m_block_pool.empty()) {
        auto block = std::move(m_block_pool.back());
        m_block_pool.pop_back();
        return block;
    }
    
    // 池空了，创建新块
    return std::make_unique<AudioBlock>();
}

void OboeAudioRenderer::ReleaseBlock(std::unique_ptr<AudioBlock> block) {
    if (!block) return;
    
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    block->clear();
    m_block_pool.push_back(std::move(block));
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat) {
    if (m_initialized.load()) {
        // 如果参数相同且流正常，直接返回成功
        if (m_sample_rate.load() == sampleRate && 
            m_channel_count.load() == channelCount &&
            m_sample_format.load() == sampleFormat &&
            m_stream_started.load()) {
            return true;
        }
        // 参数不同，先关闭
        Shutdown();
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    m_sample_format.store(sampleFormat);
    m_oboe_format = MapSampleFormat(sampleFormat);
    
    // 尝试独占模式，如果失败则回退到共享模式
    bool success = ConfigureAndOpenStreamExclusive();
    if (!success) {
        m_use_exclusive_mode.store(false);
        success = ConfigureAndOpenStream();
    }
    
    if (success) {
        m_initialized.store(true);
        m_frames_written = 0;
        m_frames_played = 0;
    }
    
    return success;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    ClearAllBuffers();
    
    m_initialized.store(false);
    m_stream_started.store(false);
    m_needs_restart.store(false);
}

void OboeAudioRenderer::ClearAllBuffers() {
    // 释放当前块
    if (m_current_block) {
        ReleaseBlock(std::move(m_current_block));
    }
    
    // 清理队列中的所有块
    std::unique_ptr<AudioBlock> block;
    while (m_audio_queue.pop(block)) {
        ReleaseBlock(std::move(block));
    }
    
    m_audio_queue.clear();
}

void OboeAudioRenderer::TrimBuffersIfNeeded() {
    int32_t buffered_frames = GetBufferedFrames();
    if (buffered_frames > MAX_BUFFERED_FRAMES) {
        // 丢弃最旧的数据块以减少延迟
        std::unique_ptr<AudioBlock> discard;
        while (GetBufferedFrames() > MAX_BUFFERED_FRAMES / 2 && m_audio_queue.pop(discard)) {
            ReleaseBlock(std::move(discard));
        }
    }
}

void OboeAudioRenderer::ConfigureForAAudio(oboe::AudioStreamBuilder& builder, bool exclusive) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(exclusive ? oboe::SharingMode::Exclusive : oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(256); // 固定回调大小
    
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

bool OboeAudioRenderer::ConfigureAndOpenStreamExclusive() {
    oboe::AudioStreamBuilder builder;
    
    ConfigureForAAudio(builder, true);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    auto result = builder.openStream(m_stream);
    if (result == oboe::Result::OK) {
        m_device_channels = m_stream->getChannelCount();
        
        // 设置合适的缓冲区大小
        int32_t framesPerBurst = m_stream->getFramesPerBurst();
        if (framesPerBurst > 0) {
            m_stream->setBufferSizeInFrames(framesPerBurst * 2);
        }
        
        result = m_stream->requestStart();
        if (result == oboe::Result::OK) {
            m_stream_started.store(true);
            return true;
        }
    }
    
    return false;
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    ConfigureForAAudio(builder, false);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    auto result = builder.openStream(m_stream);
    if (result != oboe::Result::OK) {
        // 尝试OpenSL ES作为后备
        builder.setAudioApi(oboe::AudioApi::OpenSLES);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            return false;
        }
    }
    
    m_device_channels = m_stream->getChannelCount();
    
    // 设置缓冲区大小
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    if (framesPerBurst > 0) {
        m_stream->setBufferSizeInFrames(framesPerBurst * 2);
    }
    
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        m_stream->close();
        m_stream.reset();
        return false;
    }
    
    m_stream_started.store(true);
    return true;
}

bool OboeAudioRenderer::OpenStream() {
    if (m_use_exclusive_mode.load()) {
        return ConfigureAndOpenStreamExclusive();
    } else {
        return ConfigureAndOpenStream();
    }
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
    
    // 检查是否需要重启流
    if (m_needs_restart.load()) {
        std::lock_guard<std::mutex> lock(m_stream_mutex);
        CloseStream();
        ClearAllBuffers();
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        if (!OpenStream()) {
            return false;
        }
        m_needs_restart.store(false);
    }
    
    // 修剪缓冲区以避免积累过多数据
    TrimBuffersIfNeeded();
    
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t total_bytes = num_frames * system_channels * bytes_per_sample;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    while (bytes_remaining > 0) {
        auto block = AcquireBlock();
        if (!block) {
            m_needs_restart.store(true);
            return false;
        }
        
        size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
        
        // 如果需要格式转换，在这里处理
        if (sampleFormat != m_sample_format.load()) {
            // 简单格式转换：只处理最常见的转换
            if (sampleFormat == PCM_INT16 && m_sample_format.load() == PCM_FLOAT) {
                const int16_t* src = reinterpret_cast<const int16_t*>(byte_data + bytes_processed);
                float* dst = reinterpret_cast<float*>(block->data);
                for (size_t i = 0; i < copy_size / 2; ++i) {
                    dst[i] = src[i] / 32768.0f;
                }
                copy_size = copy_size * 2; // float是int16的两倍大小
            } else {
                // 不支持转换，直接拷贝
                std::memcpy(block->data, byte_data + bytes_processed, copy_size);
            }
        } else {
            std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        }
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = m_sample_format.load(); // 使用目标格式
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) {
            // 队列满，标记需要重启
            m_needs_restart.store(true);
            return false;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
        
        // 更新写入帧数统计
        m_frames_written += num_frames;
    }
    
    // 定期调整缓冲区大小
    auto now = std::chrono::steady_clock::now();
    if (std::chrono::duration_cast<std::chrono::seconds>(now - m_last_adjust_time).count() >= 5) {
        AdjustBufferSize();
        m_last_adjust_time = now;
    }
    
    return true;
}

void OboeAudioRenderer::AdjustBufferSize() {
    if (!m_stream || !m_stream_started.load()) return;
    
    int32_t current_buffer_size = m_stream->getBufferSizeInFrames();
    int32_t buffered_frames = GetBufferedFrames();
    
    // 根据缓冲情况调整缓冲区大小
    if (buffered_frames > MAX_BUFFERED_FRAMES && current_buffer_size > 256) {
        // 减少缓冲区大小
        int32_t new_size = std::max(256, current_buffer_size / 2);
        m_stream->setBufferSizeInFrames(new_size);
    } else if (buffered_frames < MAX_BUFFERED_FRAMES / 4 && current_buffer_size < 1024) {
        // 增加缓冲区大小
        int32_t new_size = std::min(1024, current_buffer_size * 2);
        m_stream->setBufferSizeInFrames(new_size);
    }
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load()) return 0;
    
    int32_t total_frames = 0;
    int32_t device_channels = m_device_channels;
    
    // 当前块中的帧数
    if (m_current_block && !m_current_block->consumed) {
        size_t bytes_remaining = m_current_block->available();
        size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        total_frames += static_cast<int32_t>(bytes_remaining / (device_channels * bytes_per_sample));
    }
    
    // 队列中的帧数
    uint32_t queue_size = m_audio_queue.size();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / (device_channels * bytes_per_sample));
    total_frames += queue_size * frames_per_block;
    
    // 更新延迟帧数
    const_cast<std::atomic<int32_t>&>(m_latency_frames).store(total_frames);
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
    
    // 如果可能，设置流的音量
    if (m_stream) {
        m_stream->setVolume(volume);
    }
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    ClearAllBuffers();
    
    CloseStream();
    
    // 等待一小段时间
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    
    // 重新打开流
    if (m_initialized.load()) {
        OpenStream();
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

size_t OboeAudioRenderer::GetSampleSize(int32_t format) {
    return GetBytesPerSample(format);
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, 
                                                         void* audioData, 
                                                         int32_t num_frames) {
    if (!m_initialized.load() || !audioStream || !audioData) {
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t device_channels = m_device_channels;
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_needed = num_frames * device_channels * bytes_per_sample;
    
    // 清空输出缓冲区
    std::memset(audioData, 0, bytes_needed);
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_copied = 0;
    
    // 应用音量
    float volume = m_volume.load();
    
    while (bytes_copied < bytes_needed) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                ReleaseBlock(std::move(m_current_block));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 没有数据了，保持静音
                break;
            }
        }
        
        // 检查格式是否匹配
        if (m_current_block->sample_format != m_sample_format.load()) {
            // 格式不匹配，跳过
            ReleaseBlock(std::move(m_current_block));
            m_current_block.reset();
            continue;
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_needed - bytes_copied);
        
        if (bytes_to_copy == 0) {
            break;
        }
        
        // 复制数据并应用音量
        if (volume < 0.99f) {
            // 需要应用音量
            if (m_sample_format.load() == PCM_INT16) {
                int16_t* src = reinterpret_cast<int16_t*>(m_current_block->data + m_current_block->data_played);
                int16_t* dst = reinterpret_cast<int16_t*>(output + bytes_copied);
                for (size_t i = 0; i < bytes_to_copy / 2; ++i) {
                    dst[i] = static_cast<int16_t>(src[i] * volume);
                }
            } else if (m_sample_format.load() == PCM_FLOAT) {
                float* src = reinterpret_cast<float*>(m_current_block->data + m_current_block->data_played);
                float* dst = reinterpret_cast<float*>(output + bytes_copied);
                for (size_t i = 0; i < bytes_to_copy / 4; ++i) {
                    dst[i] = src[i] * volume;
                }
            } else {
                // 其他格式，直接复制
                std::memcpy(output + bytes_copied, 
                           m_current_block->data + m_current_block->data_played,
                           bytes_to_copy);
            }
        } else {
            // 音量足够高，直接复制
            std::memcpy(output + bytes_copied, 
                       m_current_block->data + m_current_block->data_played,
                       bytes_to_copy);
        }
        
        bytes_copied += bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    // 更新播放帧数统计
    m_frames_played += num_frames;
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    // 标记需要重启流，但延迟处理
    m_needs_restart.store(true);
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_stream_started.store(false);
    m_needs_restart.store(true);
}

oboe::DataCallbackResult OboeAudioRenderer::SimpleAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::SimpleErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::SimpleErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe