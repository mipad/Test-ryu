#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <android/log.h>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

// 错误处理函数
void OboeAudioRenderer::HandleError(oboe::Result error, const std::string& context) {
    __android_log_print(ANDROID_LOG_ERROR, "OboeAudioRenderer", 
                       "%s: %s", context.c_str(), oboe::convertToText(error));
    
    if (m_error_callback_user) {
        m_error_callback_user(context + ": " + oboe::convertToText(error), error);
    }
}

// 错误恢复机制
bool OboeAudioRenderer::RecoverFromError(oboe::Result error) {
    switch (error) {
        case oboe::Result::ErrorDisconnected:
            // 设备断开，等待100ms后重连
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                              "Audio device disconnected, retrying in 100ms");
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            break;
            
        case oboe::Result::ErrorInvalidState:
            // 无效状态，完全重置
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                              "Invalid audio stream state, resetting");
            Reset();
            return true;
            
        case oboe::Result::ErrorUnavailable:
            // AAudio不可用，尝试降级到OpenSL ES
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                              "AAudio unavailable, trying OpenSL ES");
            if (m_current_api == oboe::AudioApi::AAudio) {
                m_current_api = oboe::AudioApi::OpenSLES;
                return true;
            }
            break;
            
        case oboe::Result::ErrorInternal:
            // 内部错误
            __android_log_print(ANDROID_LOG_ERROR, "OboeAudioRenderer", 
                              "Internal audio error");
            break;
            
        default:
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                              "Unknown audio error: %s", oboe::convertToText(error));
            break;
    }
    
    // 通用恢复：关闭并重新打开流
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                       "Attempting generic recovery");
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    CloseStream();
    if (ConfigureAndOpenStream()) {
        return true;
    }
    
    return false;
}

// API降级
bool OboeAudioRenderer::TryFallbackApi() {
    if (m_current_api == oboe::AudioApi::AAudio) {
        m_current_api = oboe::AudioApi::OpenSLES;
        __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                           "Falling back from AAudio to OpenSL ES");
        return true;
    }
    return false;
}

// 重试机制
bool OboeAudioRenderer::TryOpenStreamWithRetry(int maxRetryCount) {
    for (int attempt = 0; attempt < maxRetryCount; ++attempt) {
        __android_log_print(ANDROID_LOG_DEBUG, "OboeAudioRenderer", 
                           "Attempt %d/%d to open audio stream", 
                           attempt + 1, maxRetryCount);
        
        if (ConfigureAndOpenStream()) {
            __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                               "Audio stream opened successfully");
            return true;
        }
        
        if (attempt < maxRetryCount - 1) {
            int delay = 50 * (1 << attempt); // 指数退避：50, 100, 200ms
            __android_log_print(ANDROID_LOG_DEBUG, "OboeAudioRenderer", 
                               "Retry in %dms", delay);
            std::this_thread::sleep_for(std::chrono::milliseconds(delay));
        }
    }
    
    __android_log_print(ANDROID_LOG_ERROR, "OboeAudioRenderer", 
                       "Failed to open audio stream after %d attempts", maxRetryCount);
    return false;
}

// OpenSL ES配置
void OboeAudioRenderer::ConfigureForOpenSLES(oboe::AudioStreamBuilder& builder) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::OpenSLES)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setFormat(m_oboe_format)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(256)
    builder.setChannelCount(m_channel_count.load())
           ->setChannelConversionAllowed(true);
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
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                       "Initializing: rate=%d, channels=%d, format=%d", 
                       sampleRate, channelCount, sampleFormat);
    
    // 首选AAudio，性能更好
    m_current_api = oboe::AudioApi::AAudio;
    
    if (!TryOpenStreamWithRetry(3)) {
        // 如果重试失败，尝试降级到OpenSL ES
        if (TryFallbackApi()) {
            if (!TryOpenStreamWithRetry(3)) {
                HandleError(oboe::Result::ErrorInternal, "Failed to open audio stream with OpenSL ES");
                return false;
            }
        } else {
            HandleError(oboe::Result::ErrorInternal, "Failed to open audio stream");
            return false;
        }
    }
    
    m_initialized.store(true);
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", "Initialized successfully");
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", "Shutting down");
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
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(256);
    
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
    
    if (m_current_api == oboe::AudioApi::AAudio) {
        ConfigureForAAudioExclusive(builder);
    } else {
        ConfigureForOpenSLES(builder);
    }
    
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        // 如果独占模式失败，尝试共享模式
        if (m_current_api == oboe::AudioApi::AAudio) {
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                               "AAudio exclusive failed, trying shared mode");
            builder.setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
        }
        
        if (result != oboe::Result::OK) {
            // 如果AAudio失败，尝试OpenSL ES
            if (m_current_api == oboe::AudioApi::AAudio) {
                __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                                   "AAudio failed, trying OpenSL ES");
                m_current_api = oboe::AudioApi::OpenSLES;
                ConfigureForOpenSLES(builder);
                result = builder.openStream(m_stream);
            }
            
            if (result != oboe::Result::OK) {
                HandleError(result, "Failed to open audio stream");
                return false;
            }
        }
    }
    
    if (!OptimizeBufferSize()) {
        CloseStream();
        return false;
    }
    
    m_device_channels = m_stream->getChannelCount();
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudioRenderer", 
                       "Device channels: %d", m_device_channels);
    
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        HandleError(result, "Failed to start audio stream");
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                       "Stream started: API=%d, rate=%d, channels=%d", 
                       m_current_api, m_stream->getSampleRate(), m_stream->getChannelCount());
    
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) return false;
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    int32_t sampleRate = m_stream->getSampleRate();
    
    // 根据设备和采样率优化缓冲区大小
    int32_t desired_buffer_size = framesPerBurst;
    
    // 针对不同场景调整
    if (m_current_api == oboe::AudioApi::AAudio) {
        // AAudio：低延迟模式
        if (sampleRate <= 48000) {
            desired_buffer_size = framesPerBurst * 2; // 标准低延迟
        } else {
            desired_buffer_size = framesPerBurst * 4; // 高采样率需要更大缓冲区
        }
    } else {
        // OpenSL ES：需要更大的缓冲区
        desired_buffer_size = framesPerBurst * 4;
    }
    
    // 设置合理的限制
    desired_buffer_size = std::max(desired_buffer_size, framesPerBurst);
    desired_buffer_size = std::min(desired_buffer_size, framesPerBurst * 8);
    
    auto result = m_stream->setBufferSizeInFrames(desired_buffer_size);
    if (result != oboe::Result::OK) {
        // 使用默认值
        desired_buffer_size = framesPerBurst * 2;
        m_stream->setBufferSizeInFrames(desired_buffer_size);
    }
    
    __android_log_print(ANDROID_LOG_DEBUG, "OboeAudioRenderer", 
                       "Buffer size optimized: %d frames (burst=%d)", 
                       desired_buffer_size, framesPerBurst);
    
    return true;
}

bool OboeAudioRenderer::OpenStream() {
    return ConfigureAndOpenStream();
}

void OboeAudioRenderer::CloseStream() {
    if (m_stream) {
        __android_log_print(ANDROID_LOG_DEBUG, "OboeAudioRenderer", "Closing audio stream");
        if (m_stream_started.load()) {
            m_stream->stop();
        }
        m_stream->close();
        m_stream.reset();
        m_stream_started.store(false);
    }
}

// 错误回调处理
void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    HandleError(error, "Stream error after close");
    
    if (!RecoverFromError(error)) {
        std::lock_guard<std::mutex> lock(m_stream_mutex);
        if (m_initialized.load()) {
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                               "Recovery failed, trying fallback API");
            CloseStream();
            if (TryFallbackApi()) {
                ConfigureAndOpenStream();
            }
        }
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    HandleError(error, "Stream error before close");
    m_stream_started.store(false);
    __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                       "Stream stopped due to error");
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
        auto block = m_object_pool.acquire();
        if (!block) return false;
        
        size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
        std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) return false;
        
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
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", "Resetting audio stream");
    m_audio_queue.clear();
    if (m_current_block) {
        m_object_pool.release(std::move(m_current_block));
    }
    
    CloseStream();
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
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                (void)m_object_pool.release(std::move(m_current_block));  // 显式忽略返回值
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 缓冲区不足，产生静音
                __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                                   "Audio buffer underrun");
                std::memset(output + bytes_copied, 0, bytes_remaining);
                break;
            }
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_remaining);
        std::memcpy(output + bytes_copied, 
                   m_current_block->data + m_current_block->data_played,
                   bytes_to_copy);
        
        // 应用音量
        if (m_volume.load() < 1.0f) {
            if (m_current_block->sample_format == PCM_INT16) {
                int16_t* samples = reinterpret_cast<int16_t*>(output + bytes_copied);
                size_t sample_count = bytes_to_copy / sizeof(int16_t);
                for (size_t i = 0; i < sample_count; i++) {
                    samples[i] = static_cast<int16_t>(samples[i] * m_volume.load());
                }
            } else if (m_current_block->sample_format == PCM_FLOAT) {
                float* samples = reinterpret_cast<float*>(output + bytes_copied);
                size_t sample_count = bytes_to_copy / sizeof(float);
                for (size_t i = 0; i < sample_count; i++) {
                    samples[i] *= m_volume.load();
                }
            }
        }
        
        bytes_copied += bytes_to_copy;
        bytes_remaining -= bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    return oboe::DataCallbackResult::Continue;
}

oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    return m_renderer->OnAudioReadyMultiFormat(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorAfterClose(
    oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorBeforeClose(
    oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe
