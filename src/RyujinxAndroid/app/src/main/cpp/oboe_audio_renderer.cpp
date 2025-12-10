#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
    StartCleanupThread();
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

void OboeAudioRenderer::StartCleanupThread() {
    m_cleanup_running = true;
    m_cleanup_thread = std::thread(&OboeAudioRenderer::CleanupThreadFunc, this);
}

void OboeAudioRenderer::StopCleanupThread() {
    m_cleanup_running = false;
    m_release_cv.notify_all();
    if (m_cleanup_thread.joinable()) {
        m_cleanup_thread.join();
    }
}

void OboeAudioRenderer::CleanupThreadFunc() {
    while (m_cleanup_running) {
        std::unique_lock<std::mutex> lock(m_release_mutex);
        
        m_release_cv.wait(lock, [this]() {
            return !m_release_queue.empty() || !m_cleanup_running;
        });
        
        if (!m_cleanup_running) {
            break;
        }
        
        std::deque<std::unique_ptr<AudioBlock>> blocks_to_release;
        blocks_to_release.swap(m_release_queue);
        
        lock.unlock();
        
        for (auto& block : blocks_to_release) {
            if (block) {
                m_object_pool.release(std::move(block));
            }
        }
        
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
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
    
    {
        std::lock_guard<std::mutex> release_lock(m_release_mutex);
        m_release_queue.clear();
    }
    
    m_current_block.reset();
    m_initialized.store(false);
    m_stream_started.store(false);
    
    StopCleanupThread();
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
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
        // 格式不匹配，先清空缓冲区
        Flush();
        return false;
    }
    
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t frame_size = system_channels * bytes_per_sample;
    size_t total_bytes = num_frames * frame_size;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    while (bytes_remaining > 0) {
        auto block = m_object_pool.acquire();
        if (!block) return false;
        
        // 确保每个块都是帧对齐的
        size_t max_frames_in_block = AudioBlock::BLOCK_SIZE / frame_size;
        if (max_frames_in_block == 0) {
            m_object_pool.release(std::move(block));
            return false;
        }
        
        size_t max_copy_bytes = max_frames_in_block * frame_size;
        size_t copy_size = std::min(bytes_remaining, max_copy_bytes);
        
        // 确保copy_size是帧大小的整数倍
        size_t aligned_copy_size = (copy_size / frame_size) * frame_size;
        if (aligned_copy_size == 0) {
            m_object_pool.release(std::move(block));
            return false;
        }
        
        std::memcpy(block->data, byte_data + bytes_processed, aligned_copy_size);
        block->data_size = aligned_copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) {
            return false;
        }
        
        bytes_processed += aligned_copy_size;
        bytes_remaining -= aligned_copy_size;
    }
    
    return true;
}

void OboeAudioRenderer::Flush() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_audio_queue.clear();
    
    {
        std::lock_guard<std::mutex> release_lock(m_release_mutex);
        m_release_queue.clear();
    }
    
    if (m_current_block) {
        m_object_pool.release(std::move(m_current_block));
    }
    
    // 不清除流，只清空数据
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load()) return 0;
    
    int32_t total_frames = 0;
    int32_t device_channels = m_device_channels;
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    
    if (device_channels == 0 || bytes_per_sample == 0) {
        return 0;
    }
    
    size_t frame_size = device_channels * bytes_per_sample;
    
    if (m_current_block && !m_current_block->consumed) {
        size_t bytes_remaining = m_current_block->available();
        total_frames += static_cast<int32_t>(bytes_remaining / frame_size);
    }
    
    uint32_t queue_size = m_audio_queue.size();
    int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / frame_size);
    total_frames += queue_size * frames_per_block;
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_audio_queue.clear();
    
    {
        std::lock_guard<std::mutex> release_lock(m_release_mutex);
        m_release_queue.clear();
    }
    
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
    
    // 先清空输出缓冲区
    std::memset(output, 0, bytes_remaining);
    
    while (bytes_remaining > 0) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                std::lock_guard<std::mutex> lock(m_release_mutex);
                m_release_queue.push_back(std::move(m_current_block));
                m_release_cv.notify_one();
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 没有更多数据，保持静音
                break;
            }
        }
        
        // 检查格式是否匹配
        if (m_current_block->sample_format != m_sample_format.load()) {
            // 格式不匹配，跳过这个块
            std::lock_guard<std::mutex> lock(m_release_mutex);
            m_release_queue.push_back(std::move(m_current_block));
            m_release_cv.notify_one();
            m_current_block.reset();
            continue;
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_remaining);
        
        // 确保拷贝的数据是帧对齐的
        int32_t system_channels = m_channel_count.load();
        size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        size_t frame_size = system_channels * bytes_per_sample;
        size_t aligned_bytes_to_copy = (bytes_to_copy / frame_size) * frame_size;
        
        if (aligned_bytes_to_copy == 0) {
            // 数据不是帧对齐的，跳过这个块
            std::lock_guard<std::mutex> lock(m_release_mutex);
            m_release_queue.push_back(std::move(m_current_block));
            m_release_cv.notify_one();
            m_current_block.reset();
            continue;
        }
        
        std::memcpy(output + bytes_copied, 
                   m_current_block->data + m_current_block->data_played,
                   aligned_bytes_to_copy);
        
        bytes_copied += aligned_bytes_to_copy;
        bytes_remaining -= aligned_bytes_to_copy;
        m_current_block->data_played += aligned_bytes_to_copy;
        
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