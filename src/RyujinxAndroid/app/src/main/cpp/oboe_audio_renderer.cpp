#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <span>
#include <limits>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() 
    : m_audio_callback(std::make_unique<AAudioExclusiveCallback>(this)),
      m_error_callback(std::make_unique<AAudioExclusiveErrorCallback>(this)) {
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, 
                                             int32_t sampleFormat) {
    if (m_initialized.load(std::memory_order_acquire)) {
        if (m_sample_rate.load(std::memory_order_relaxed) != sampleRate || 
            m_channel_count.load(std::memory_order_relaxed) != channelCount ||
            m_sample_format.load(std::memory_order_relaxed) != sampleFormat) {
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate, std::memory_order_relaxed);
    m_channel_count.store(channelCount, std::memory_order_relaxed);
    m_sample_format.store(sampleFormat, std::memory_order_relaxed);
    m_oboe_format = MapSampleFormat(sampleFormat);
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true, std::memory_order_release);
    return true;
}

void OboeAudioRenderer::Shutdown() noexcept {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    m_audio_queue.clear();
    m_current_block.reset();
    m_initialized.store(false, std::memory_order_release);
    m_stream_started.store(false, std::memory_order_release);
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) const noexcept {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load(std::memory_order_relaxed))
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(256);
    
    const auto channel_count = m_channel_count.load(std::memory_order_relaxed);
    const auto channel_mask = [channel_count]() noexcept -> oboe::ChannelMask {
        switch (channel_count) {
        case 1:  return oboe::ChannelMask::Mono;
        case 2:  return oboe::ChannelMask::Stereo;
        case 6:  return oboe::ChannelMask::CM5Point1;
        default: return oboe::ChannelMask::Unspecified;
        }
    }();
    
    builder.setChannelCount(channel_count)
           ->setChannelMask(channel_mask)
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() noexcept {
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
    
    m_stream_started.store(true, std::memory_order_release);
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() noexcept {
    if (!m_stream) return false;
    
    const int32_t framesPerBurst = m_stream->getFramesPerBurst();
    const int32_t desired_buffer_size = framesPerBurst > 0 ? framesPerBurst * 2 : 960;
    
    m_stream->setBufferSizeInFrames(desired_buffer_size);
    return true;
}

bool OboeAudioRenderer::OpenStream() noexcept {
    return ConfigureAndOpenStream();
}

void OboeAudioRenderer::CloseStream() noexcept {
    if (m_stream) {
        if (m_stream_started.load(std::memory_order_acquire)) {
            m_stream->stop();
        }
        m_stream->close();
        m_stream.reset();
        m_stream_started.store(false, std::memory_order_release);
    }
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) noexcept {
    if (!m_initialized.load(std::memory_order_acquire) || !data || num_frames <= 0) {
        return false;
    }
    
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, 
                                      int32_t sampleFormat) noexcept {
    if (!m_initialized.load(std::memory_order_acquire) || !data || num_frames <= 0) {
        return false;
    }
    
    const int32_t system_channels = m_channel_count.load(std::memory_order_relaxed);
    const size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    const size_t total_bytes = static_cast<size_t>(num_frames) * 
                               static_cast<size_t>(system_channels) * 
                               bytes_per_sample;
    
    const auto* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    while (bytes_remaining > 0) {
        auto block = m_object_pool.acquire();
        if (!block) return false;
        
        const size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
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

int32_t OboeAudioRenderer::GetBufferedFrames() const noexcept {
    if (!m_initialized.load(std::memory_order_acquire)) return 0;
    
    int32_t total_frames = 0;
    const int32_t device_channels = m_device_channels;
    
    if (m_current_block && !m_current_block->consumed) {
        const size_t bytes_remaining = m_current_block->available();
        const size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        total_frames += static_cast<int32_t>(bytes_remaining / 
                                            (static_cast<size_t>(device_channels) * bytes_per_sample));
    }
    
    const uint32_t queue_size = m_audio_queue.size();
    const size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load(std::memory_order_relaxed));
    const int32_t frames_per_block = static_cast<int32_t>(
        AudioBlock::BLOCK_SIZE / (static_cast<size_t>(device_channels) * bytes_per_sample));
    total_frames += static_cast<int32_t>(queue_size) * frames_per_block;
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) noexcept {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f), std::memory_order_release);
}

void OboeAudioRenderer::Reset() noexcept {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_audio_queue.clear();
    if (m_current_block) {
        static_cast<void>(m_object_pool.release(std::move(m_current_block)));
    }
    
    CloseStream();
    static_cast<void>(ConfigureAndOpenStream());
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) noexcept {
    switch (format) {
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        default:         return oboe::AudioFormat::I16;
    }
}

size_t OboeAudioRenderer::GetBytesPerSample(int32_t format) noexcept {
    switch (format) {
        case PCM_INT16:  return 2;
        case PCM_INT24:  return 3;
        case PCM_INT32:  return 4;
        case PCM_FLOAT:  return 4;
        default:         return 2;
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) noexcept {
    
    if (!m_initialized.load(std::memory_order_acquire)) {
        const int32_t channels = m_device_channels;
        const size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load(std::memory_order_relaxed));
        const size_t bytes_requested = static_cast<size_t>(num_frames) * 
                                       static_cast<size_t>(channels) * 
                                       bytes_per_sample;
        std::memset(audioData, 0, bytes_requested);
        return oboe::DataCallbackResult::Continue;
    }
    
    auto* output = static_cast<uint8_t*>(audioData);
    size_t bytes_remaining = static_cast<size_t>(num_frames) * 
                             static_cast<size_t>(m_device_channels) * 
                             GetBytesPerSample(m_sample_format.load(std::memory_order_relaxed));
    size_t bytes_copied = 0;
    
    while (bytes_remaining > 0) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                static_cast<void>(m_object_pool.release(std::move(m_current_block)));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                std::memset(output + bytes_copied, 0, bytes_remaining);
                break;
            }
        }
        
        const size_t bytes_to_copy = std::min(m_current_block->available(), bytes_remaining);
        std::memcpy(output + bytes_copied, 
                   m_current_block->data + m_current_block->data_played,
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

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, 
                                                oboe::Result error) noexcept {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_initialized.load(std::memory_order_acquire)) {
        CloseStream();
        static_cast<void>(ConfigureAndOpenStream());
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, 
                                                 oboe::Result error) noexcept {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false, std::memory_order_release);
}

oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) noexcept {
    return m_renderer->OnAudioReadyMultiFormat(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorAfterClose(
    oboe::AudioStream* audioStream, oboe::Result error) noexcept {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorBeforeClose(
    oboe::AudioStream* audioStream, oboe::Result error) noexcept {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe