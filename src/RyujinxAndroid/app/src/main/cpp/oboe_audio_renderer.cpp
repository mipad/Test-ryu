#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<SimpleAudioCallback>(this);
    m_error_callback = std::make_unique<SimpleErrorCallback>(this);
    InitializePool();
    m_last_clock_update = std::chrono::steady_clock::now();
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

void OboeAudioRenderer::InitializePool() {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    const size_t POOL_SIZE = 64;
    m_block_pool.reserve(POOL_SIZE);
    
    for (size_t i = 0; i < POOL_SIZE; ++i) {
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
    auto block = std::make_unique<AudioBlock>();
    block->clear();
    return block;
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
    
    // 重置时钟
    m_total_frames_played.store(0);
    m_total_frames_written.store(0);
    m_clock_drift_correction = 0;
    m_last_clock_update = std::chrono::steady_clock::now();
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
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
    m_audio_queue.clear();
    
    if (m_current_block) {
        ReleaseBlock(std::move(m_current_block));
    }
    
    // 清理队列中的所有块
    std::unique_ptr<AudioBlock> block;
    while (m_audio_queue.pop(block)) {
        ReleaseBlock(std::move(block));
    }
}

void OboeAudioRenderer::Flush() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    ClearAllBuffers();
    m_total_frames_written.store(m_total_frames_played.load());
}

void OboeAudioRenderer::ConfigureForAAudio(oboe::AudioStreamBuilder& builder) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(480);  // 增加回调帧数，减少回调频率
    
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
    
    ConfigureForAAudio(builder);
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
    m_needs_restart.store(false);
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) return false;
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    int32_t desired_buffer_size = framesPerBurst > 0 ? framesPerBurst * 4 : 1920;  // 增加缓冲区大小
    
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

bool OboeAudioRenderer::TryRestartStream() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (!m_initialized.load()) {
        return false;
    }
    
    CloseStream();
    ClearAllBuffers();
    
    // 等待一小段时间，让系统有机会恢复
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    
    bool success = ConfigureAndOpenStream();
    
    if (success) {
        m_needs_restart.store(false);
    }
    
    return success;
}

void OboeAudioRenderer::UpdateAudioClock(int32_t frames_played) {
    auto now = std::chrono::steady_clock::now();
    auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_clock_update);
    
    m_total_frames_played.fetch_add(frames_played);
    
    // 每秒钟检查一次时钟漂移
    if (elapsed.count() > 1000) {
        int64_t expected_frames = (m_sample_rate.load() * elapsed.count()) / 1000;
        int64_t actual_frames = frames_played;
        
        // 计算时钟漂移
        if (expected_frames > 0) {
            int32_t drift_percent = static_cast<int32_t>((abs(expected_frames - actual_frames) * 100) / expected_frames);
            
            // 如果漂移超过5%，可能需要调整
            if (drift_percent > 5) {
                m_clock_drift_correction = static_cast<int32_t>(expected_frames - actual_frames);
                
                // 如果漂移太大，强制重新同步
                if (abs(m_clock_drift_correction) > (m_sample_rate.load() / 10)) { // 超过0.1秒
                    m_needs_restart.store(true);
                }
            }
        }
        
        m_last_clock_update = now;
    }
}

int64_t OboeAudioRenderer::GetCurrentAudioPosition() const {
    return m_total_frames_played.load();
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
        if (!TryRestartStream()) {
            return false;
        }
    }
    
    // 检查格式是否匹配
    if (sampleFormat != m_sample_format.load()) {
        return false;
    }
    
    // 检查缓冲区是否过满，防止延迟累积
    int32_t buffered = GetBufferedFrames();
    if (buffered > (m_sample_rate.load() / 2)) { // 超过0.5秒的缓冲
        // 丢弃一些旧数据，减少延迟
        Flush();
    }
    
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t total_bytes = num_frames * system_channels * bytes_per_sample;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    while (bytes_remaining > 0) {
        auto block = AcquireBlock();
        if (!block) return false;
        
        size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
        std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) {
            // 队列满了，刷新缓冲区并重试
            Flush();
            return false;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
    }
    
    m_total_frames_written.fetch_add(num_frames);
    
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
    
    uint32_t queue_size = m_audio_queue.size();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / (device_channels * bytes_per_sample));
    total_frames += queue_size * frames_per_block;
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    ClearAllBuffers();
    
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

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !audioStream || !audioData) {
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t device_channels = m_device_channels;
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_needed = num_frames * device_channels * bytes_per_sample;
    
    // 先清空输出缓冲区
    std::memset(audioData, 0, bytes_needed);
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_copied = 0;
    
    while (bytes_copied < bytes_needed) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                ReleaseBlock(std::move(m_current_block));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 没有更多数据了，跳出循环（剩余部分已经是静音）
                break;
            }
        }
        
        // 检查格式是否匹配
        if (m_current_block->sample_format != m_sample_format.load()) {
            // 格式不匹配，跳过这个块
            ReleaseBlock(std::move(m_current_block));
            m_current_block.reset();
            continue;
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_needed - bytes_copied);
        
        if (bytes_to_copy == 0) {
            break;
        }
        
        std::memcpy(output + bytes_copied, 
                   m_current_block->data + m_current_block->data_played,
                   bytes_to_copy);
        
        bytes_copied += bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    // 更新音频时钟
    if (bytes_copied > 0) {
        int32_t frames_played = static_cast<int32_t>(bytes_copied / (device_channels * bytes_per_sample));
        UpdateAudioClock(frames_played);
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    // 标记需要重启流
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