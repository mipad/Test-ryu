#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() 
    : m_last_buffer_time(std::chrono::steady_clock::now()) {
    m_callback = std::make_unique<AudioStreamCallback>(this);
    InitializePool(64);
    OBOE_LOGI("OboeAudioRenderer created");
}

OboeAudioRenderer::~OboeAudioRenderer() {
    OBOE_LOGI("OboeAudioRenderer destroying");
    Shutdown();
}

void OboeAudioRenderer::InitializePool(size_t pool_size) {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    pool_size = std::min(pool_size, MAX_POOL_SIZE);
    m_block_pool.reserve(pool_size);
    
    for (size_t i = 0; i < pool_size; ++i) {
        auto block = std::make_unique<AudioBlock>();
        block->clear();
        m_block_pool.push_back(std::move(block));
    }
    
    OBOE_LOGD("Initialized pool with %zu blocks", pool_size);
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
    OBOE_LOGW("Pool exhausted, creating new block");
    return block;
}

void OboeAudioRenderer::ReleaseBlock(std::unique_ptr<AudioBlock> block) {
    if (!block) return;
    
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    block->clear();
    
    if (m_block_pool.size() < MAX_POOL_SIZE) {
        m_block_pool.push_back(std::move(block));
    }
    // 如果池已满，自动释放块
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat) {
    OBOE_LOGI("InitializeWithFormat: rate=%d, channels=%d, format=%d", 
              sampleRate, channelCount, sampleFormat);
    
    if (sampleRate <= 0 || channelCount <= 0) {
        OBOE_LOGE("Invalid parameters: sampleRate=%d, channelCount=%d", sampleRate, channelCount);
        return false;
    }
    
    if (m_initialized.load()) {
        if (m_sample_rate.load() == sampleRate && 
            m_channel_count.load() == channelCount &&
            m_sample_format.load() == sampleFormat) {
            OBOE_LOGD("Already initialized with same parameters");
            return true;
        }
        
        OBOE_LOGI("Reinitializing with new parameters");
        Shutdown();
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    m_sample_format.store(sampleFormat);
    m_oboe_format = MapSampleFormat(sampleFormat);
    m_error_count.store(0);
    
    if (!ConfigureAndOpenStream()) {
        OBOE_LOGE("Failed to configure and open stream");
        return false;
    }
    
    m_initialized.store(true);
    m_current_volume = 1.0f;
    m_target_volume.store(1.0f);
    
    OBOE_LOGI("OboeAudioRenderer initialized successfully");
    return true;
}

void OboeAudioRenderer::Shutdown() {
    OBOE_LOGI("Shutting down OboeAudioRenderer");
    
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    ClearAllBuffers();
    
    m_initialized.store(false);
    m_stream_active.store(false);
    m_recovery_pending.store(false);
    
    OBOE_LOGI("OboeAudioRenderer shutdown complete");
}

void OboeAudioRenderer::ClearAllBuffers() {
    OBOE_LOGD("Clearing all buffers");
    
    m_audio_queue.clear();
    
    if (m_current_block) {
        ReleaseBlock(std::move(m_current_block));
    }
    
    // 清理队列中的所有块
    std::unique_ptr<AudioBlock> block;
    while (m_audio_queue.pop(block)) {
        ReleaseBlock(std::move(block));
    }
    
    m_total_written_frames.store(0);
    m_total_played_frames.store(0);
}

void OboeAudioRenderer::ConfigureStreamBuilder(oboe::AudioStreamBuilder& builder) {
    auto sampleRate = m_sample_rate.load();
    auto channelCount = m_channel_count.load();
    auto format = m_oboe_format;
    
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setFormat(format)
           ->setChannelCount(channelCount)
           ->setSampleRate(sampleRate)
           ->setFormatConversionAllowed(true)
           ->setChannelConversionAllowed(true)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(256)
           ->setCallback(m_callback.get());
    
    // 设置声道掩码
    switch (channelCount) {
        case 1:
            builder.setChannelMask(oboe::ChannelMask::Mono);
            break;
        case 2:
            builder.setChannelMask(oboe::ChannelMask::Stereo);
            break;
        case 6:
            builder.setChannelMask(oboe::ChannelMask::CM5Point1);
            break;
        default:
            builder.setChannelMask(oboe::ChannelMask::Unspecified);
            break;
    }
    
    // 首选AAudio，但允许回退到OpenSL ES
    builder.setAudioApi(oboe::AudioApi::AAudio);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    ConfigureStreamBuilder(builder);
    
    // 尝试使用首选配置打开流
    oboe::Result result = builder.openStream(m_stream);
    
    // 如果失败，尝试降级配置
    if (result != oboe::Result::OK) {
        OBOE_LOGW("Failed to open stream with preferred config, trying fallback: %s", 
                  oboe::convertToText(result));
        
        // 降级到OpenSL ES
        builder.setAudioApi(oboe::AudioApi::OpenSLES);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            OBOE_LOGE("Failed to open stream with OpenSLES: %s", 
                      oboe::convertToText(result));
            return false;
        }
        
        OBOE_LOGI("Successfully opened stream with OpenSLES");
    }
    
    if (!m_stream) {
        OBOE_LOGE("Stream is null after opening");
        return false;
    }
    
    // 获取实际设备参数
    m_device_channels = m_stream->getChannelCount();
    OBOE_LOGI("Stream opened: channels=%d (requested=%d), sampleRate=%d, format=%d", 
              m_device_channels, m_channel_count.load(),
              m_stream->getSampleRate(), m_stream->getFormat());
    
    // 优化缓冲区大小
    if (!OptimizeBufferSize()) {
        OBOE_LOGW("Failed to optimize buffer size, but continuing");
    }
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        OBOE_LOGE("Failed to start stream: %s", oboe::convertToText(result));
        CloseStream();
        return false;
    }
    
    m_stream_active.store(true);
    m_recovery_pending.store(false);
    m_last_buffer_time = std::chrono::steady_clock::now();
    
    OBOE_LOGI("Stream started successfully");
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) {
        return false;
    }
    
    try {
        int32_t framesPerBurst = m_stream->getFramesPerBurst();
        if (framesPerBurst <= 0) {
            framesPerBurst = 192; // 默认值
        }
        
        int32_t desiredBufferSize = framesPerBurst * 4; // 4个突发
        int32_t maxBufferSize = m_stream->getBufferCapacityInFrames();
        
        if (desiredBufferSize > maxBufferSize) {
            desiredBufferSize = maxBufferSize;
        }
        
        auto result = m_stream->setBufferSizeInFrames(desiredBufferSize);
        if (result != oboe::Result::OK) {
            OBOE_LOGW("Failed to set buffer size: %s (requested=%d, capacity=%d)", 
                      oboe::convertToText(result), desiredBufferSize, maxBufferSize);
            return false;
        }
        
        int32_t actualBufferSize = m_stream->getBufferSizeInFrames();
        OBOE_LOGD("Buffer optimized: requested=%d, actual=%d, capacity=%d, burst=%d",
                  desiredBufferSize, actualBufferSize, maxBufferSize, framesPerBurst);
        
        return true;
    } catch (const std::exception& e) {
        OBOE_LOGE("Exception in OptimizeBufferSize: %s", e.what());
        return false;
    }
}

bool OboeAudioRenderer::OpenStream() {
    return ConfigureAndOpenStream();
}

void OboeAudioRenderer::CloseStream() {
    if (m_stream) {
        OBOE_LOGD("Closing stream");
        
        try {
            if (m_stream_active.load()) {
                m_stream->stop();
            }
            m_stream->close();
        } catch (const std::exception& e) {
            OBOE_LOGE("Exception while closing stream: %s", e.what());
        }
        
        m_stream.reset();
        m_stream_active.store(false);
    }
}

bool OboeAudioRenderer::TryRecoverStream() {
    OBOE_LOGI("Attempting to recover stream");
    
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (!m_initialized.load()) {
        OBOE_LOGW("Cannot recover: renderer not initialized");
        return false;
    }
    
    int32_t errorCount = m_error_count.load();
    if (errorCount > 10) {
        OBOE_LOGE("Too many errors (%d), giving up on recovery", errorCount);
        return false;
    }
    
    CloseStream();
    ClearAllBuffers();
    
    // 等待系统稳定
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    
    bool success = ConfigureAndOpenStream();
    
    if (success) {
        m_error_count.store(0);
        m_recovery_pending.store(false);
        OBOE_LOGI("Stream recovery successful");
    } else {
        m_error_count.fetch_add(1);
        OBOE_LOGE("Stream recovery failed (attempt %d)", errorCount + 1);
    }
    
    return success;
}

bool OboeAudioRenderer::RestartStream() {
    return TryRecoverStream();
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    int32_t channels = m_channel_count.load();
    size_t data_size = num_frames * channels * sizeof(int16_t);
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        OBOE_LOGW("WriteAudioRaw: invalid parameters");
        return false;
    }
    
    // 检查是否需要恢复流
    if (m_recovery_pending.load()) {
        OBOE_LOGW("Recovery pending, attempting to restart stream");
        if (!TryRecoverStream()) {
            return false;
        }
    }
    
    // 检查格式是否匹配
    if (sampleFormat != m_sample_format.load()) {
        OBOE_LOGW("WriteAudioRaw: sample format mismatch (got=%d, expected=%d)", 
                  sampleFormat, m_sample_format.load());
        return false;
    }
    
    // 检查队列是否过载
    uint32_t queue_size = m_audio_queue.size();
    if (queue_size >= AUDIO_QUEUE_SIZE * 3 / 4) {
        OBOE_LOGW("Audio queue is getting full: %u/%u", queue_size, AUDIO_QUEUE_SIZE);
        m_overrun_count.fetch_add(1);
    }
    
    int32_t channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t total_bytes = num_frames * channels * bytes_per_sample;
    size_t frames_written = 0;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    
    while (frames_written < num_frames) {
        auto block = AcquireBlock();
        if (!block) {
            OBOE_LOGE("Failed to acquire block");
            return false;
        }
        
        size_t bytes_remaining = total_bytes - (frames_written * channels * bytes_per_sample);
        size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
        
        // 确保不会复制超过块大小的数据
        if (copy_size > AudioBlock::BLOCK_SIZE) {
            OBOE_LOGE("Copy size exceeds block size: %zu > %zu", copy_size, AudioBlock::BLOCK_SIZE);
            ReleaseBlock(std::move(block));
            return false;
        }
        
        std::memcpy(block->data, byte_data + (frames_written * channels * bytes_per_sample), copy_size);
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) {
            OBOE_LOGW("Audio queue is full, dropping data");
            m_overrun_count.fetch_add(1);
            return false;
        }
        
        size_t frames_in_block = copy_size / (channels * bytes_per_sample);
        frames_written += frames_in_block;
    }
    
    m_total_written_frames.fetch_add(num_frames);
    m_last_buffer_time = std::chrono::steady_clock::now();
    
    return true;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load() || !m_stream_active.load()) {
        return 0;
    }
    
    try {
        int32_t total_frames = 0;
        int32_t device_channels = m_device_channels;
        
        // 当前正在处理的块
        if (m_current_block && !m_current_block->consumed) {
            size_t bytes_remaining = m_current_block->available();
            size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
            if (device_channels > 0 && bytes_per_sample > 0) {
                total_frames += static_cast<int32_t>(bytes_remaining / (device_channels * bytes_per_sample));
            }
        }
        
        // 队列中的块
        uint32_t queue_size = m_audio_queue.size();
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        
        if (device_channels > 0 && bytes_per_sample > 0) {
            int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / (device_channels * bytes_per_sample));
            total_frames += queue_size * frames_per_block;
        }
        
        // 加上流中的缓冲区
        if (m_stream) {
            auto result = m_stream->getAvailableFrames();
            if (result) {
                total_frames += result.value();
            }
        }
        
        return total_frames;
    } catch (const std::exception& e) {
        OBOE_LOGE("Exception in GetBufferedFrames: %s", e.what());
        return 0;
    }
}

float OboeAudioRenderer::GetLatencyMs() const {
    if (!m_stream || !m_stream_active.load()) {
        return 0.0f;
    }
    
    try {
        auto result = m_stream->calculateLatencyMillis();
        if (result) {
            return result.value();
        }
    } catch (const std::exception& e) {
        OBOE_LOGE("Exception calculating latency: %s", e.what());
    }
    
    return 0.0f;
}

void OboeAudioRenderer::SetVolume(float volume) {
    volume = std::max(0.0f, std::min(1.0f, volume));
    m_target_volume.store(volume);
}

void OboeAudioRenderer::ApplyVolume(void* audioData, int32_t num_frames, int32_t format) {
    if (m_current_volume == m_target_volume.load() && m_current_volume == 1.0f) {
        return; // 无音量调整需要
    }
    
    // 平滑音量过渡
    float target = m_target_volume.load();
    if (std::abs(m_current_volume - target) > VOLUME_RAMP_SPEED) {
        if (m_current_volume < target) {
            m_current_volume = std::min(m_current_volume + VOLUME_RAMP_SPEED, target);
        } else {
            m_current_volume = std::max(m_current_volume - VOLUME_RAMP_SPEED, target);
        }
    } else {
        m_current_volume = target;
    }
    
    if (m_current_volume == 1.0f) {
        return; // 全音量，无需处理
    }
    
    switch (format) {
        case PCM_INT16: {
            int16_t* samples = static_cast<int16_t*>(audioData);
            int32_t num_samples = num_frames * m_device_channels;
            for (int32_t i = 0; i < num_samples; ++i) {
                samples[i] = static_cast<int16_t>(samples[i] * m_current_volume);
            }
            break;
        }
        case PCM_FLOAT: {
            float* samples = static_cast<float*>(audioData);
            int32_t num_samples = num_frames * m_device_channels;
            for (int32_t i = 0; i < num_samples; ++i) {
                samples[i] *= m_current_volume;
            }
            break;
        }
        default:
            break;
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !audioStream || !audioData || num_frames <= 0) {
        return oboe::DataCallbackResult::Continue;
    }
    
    ProcessAudioData(audioData, num_frames);
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::ProcessAudioData(void* audioData, int32_t num_frames) {
    try {
        int32_t device_channels = m_device_channels;
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        size_t bytes_needed = num_frames * device_channels * bytes_per_sample;
        
        // 初始化输出缓冲区为零（静音）
        std::memset(audioData, 0, bytes_needed);
        
        uint8_t* output = static_cast<uint8_t*>(audioData);
        size_t bytes_copied = 0;
        int32_t frames_copied = 0;
        
        while (frames_copied < num_frames) {
            // 获取下一个块
            if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
                if (m_current_block) {
                    ReleaseBlock(std::move(m_current_block));
                    m_current_block.reset();
                }
                
                if (!m_audio_queue.pop(m_current_block)) {
                    // 没有更多数据，填充静音
                    OBOE_LOGD("Audio underrun: no data available");
                    m_underrun_count.fetch_add(1);
                    break;
                }
            }
            
            // 检查格式是否匹配
            if (m_current_block->sample_format != m_sample_format.load()) {
                OBOE_LOGW("Block format mismatch, skipping");
                ReleaseBlock(std::move(m_current_block));
                m_current_block.reset();
                continue;
            }
            
            size_t bytes_available = m_current_block->available();
            size_t bytes_to_copy = std::min(bytes_available, bytes_needed - bytes_copied);
            
            if (bytes_to_copy == 0) {
                break;
            }
            
            std::memcpy(output + bytes_copied, 
                       m_current_block->data + m_current_block->data_played,
                       bytes_to_copy);
            
            bytes_copied += bytes_to_copy;
            m_current_block->data_played += bytes_to_copy;
            frames_copied = bytes_copied / (device_channels * bytes_per_sample);
            
            if (m_current_block->available() == 0) {
                m_current_block->consumed = true;
            }
        }
        
        // 应用音量控制
        ApplyVolume(audioData, frames_copied, m_sample_format.load());
        
        m_total_played_frames.fetch_add(frames_copied);
        
        // 检查长时间无数据
        auto now = std::chrono::steady_clock::now();
        auto time_since_last_buffer = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_buffer_time).count();
        if (time_since_last_buffer > 1000 && m_audio_queue.size() == 0) {
            OBOE_LOGW("No audio data for %lld ms", time_since_last_buffer);
        }
        
    } catch (const std::exception& e) {
        OBOE_LOGE("Exception in ProcessAudioData: %s", e.what());
    }
}

void OboeAudioRenderer::OnErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    OBOE_LOGE("Stream error after close: %s", oboe::convertToText(error));
    m_stream_active.store(false);
    m_recovery_pending.store(true);
}

void OboeAudioRenderer::OnErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    OBOE_LOGE("Stream error before close: %s", oboe::convertToText(error));
    m_stream_active.store(false);
    m_recovery_pending.store(true);
}

void OboeAudioRenderer::Reset() {
    OBOE_LOGI("Resetting audio renderer");
    
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    ClearAllBuffers();
    
    // 重新初始化流
    if (m_initialized.load()) {
        CloseStream();
        ConfigureAndOpenStream();
    }
    
    OBOE_LOGI("Audio renderer reset complete");
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        default:         
            OBOE_LOGW("Unknown sample format %d, defaulting to I16", format);
            return oboe::AudioFormat::I16;
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

// 回调类的实现
oboe::DataCallbackResult OboeAudioRenderer::AudioStreamCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AudioStreamCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AudioStreamCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe