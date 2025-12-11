#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

// 简单的LockFreeQueue实现
template <typename T, uint32_t CAPACITY>
class LockFreeQueue {
public:
    static_assert((CAPACITY & (CAPACITY - 1)) == 0, "CAPACITY must be a power of 2");
    
    bool pop(T &val) {
        uint32_t currentRead = m_readCounter.load(std::memory_order_relaxed);
        uint32_t currentWrite = m_writeCounter.load(std::memory_order_acquire);
        
        if (currentRead == currentWrite) {
            return false;
        }
        
        val = std::move(m_buffer[currentRead & (CAPACITY - 1)]);
        m_readCounter.store(currentRead + 1, std::memory_order_release);
        return true;
    }

    bool push(T&& item) {
        uint32_t currentWrite = m_writeCounter.load(std::memory_order_relaxed);
        uint32_t currentRead = m_readCounter.load(std::memory_order_acquire);
        
        if ((currentWrite - currentRead) == CAPACITY) {
            return false;
        }
        
        m_buffer[currentWrite & (CAPACITY - 1)] = std::move(item);
        m_writeCounter.store(currentWrite + 1, std::memory_order_release);
        return true;
    }

    uint32_t size() const {
        uint32_t currentWrite = m_writeCounter.load(std::memory_order_acquire);
        uint32_t currentRead = m_readCounter.load(std::memory_order_acquire);
        return currentWrite - currentRead;
    }

    void clear() {
        uint32_t currentWrite = m_writeCounter.load(std::memory_order_acquire);
        m_readCounter.store(currentWrite, std::memory_order_release);
    }

private:
    T m_buffer[CAPACITY];
    std::atomic<uint32_t> m_writeCounter{0};
    std::atomic<uint32_t> m_readCounter{0};
};

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() {
    m_callback = std::make_unique<AudioStreamCallback>(this);
    m_audio_queue = std::make_unique<LockFreeQueue<std::unique_ptr<AudioBlock>, AUDIO_QUEUE_SIZE>>();
    InitializePool(64);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

void OboeAudioRenderer::InitializePool(uint32_t pool_size) {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    if (pool_size > MAX_POOL_SIZE) {
        pool_size = MAX_POOL_SIZE;
    }
    
    m_block_pool.reserve(pool_size);
    
    for (uint32_t i = 0; i < pool_size; ++i) {
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
    
    return std::make_unique<AudioBlock>();
}

void OboeAudioRenderer::ReleaseBlock(std::unique_ptr<AudioBlock> block) {
    if (!block) return;
    
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    block->clear();
    
    if (m_block_pool.size() < MAX_POOL_SIZE) {
        m_block_pool.push_back(std::move(block));
    }
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat) {
    if (sampleRate <= 0 || channelCount <= 0) {
        return false;
    }
    
    if (m_initialized.load()) {
        if (m_sample_rate.load() == sampleRate && 
            m_channel_count.load() == channelCount &&
            m_sample_format.load() == sampleFormat) {
            return true;
        }
        
        Shutdown();
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
    m_current_volume = 1.0f;
    m_target_volume = 1.0f;
    
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    ClearAllBuffers();
    
    m_initialized.store(false);
    m_stream_active.store(false);
    m_recovery_pending.store(false);
}

void OboeAudioRenderer::ClearAllBuffers() {
    m_audio_queue->clear();
    
    if (m_current_block) {
        ReleaseBlock(std::move(m_current_block));
    }
    
    std::unique_ptr<AudioBlock> block;
    while (m_audio_queue->pop(block)) {
        ReleaseBlock(std::move(block));
    }
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
           ->setContentType(oboe::ContentType::Game)
           ->setFramesPerCallback(256)
           ->setCallback(m_callback.get());
    
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
    }
    
    builder.setAudioApi(oboe::AudioApi::AAudio);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    ConfigureStreamBuilder(builder);
    
    oboe::Result result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        builder.setAudioApi(oboe::AudioApi::OpenSLES);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            return false;
        }
    }
    
    if (!m_stream) {
        return false;
    }
    
    m_device_channels = m_stream->getChannelCount();
    
    if (!OptimizeBufferSize()) {
        CloseStream();
        return false;
    }
    
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        CloseStream();
        return false;
    }
    
    m_stream_active.store(true);
    m_recovery_pending.store(false);
    
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) {
        return false;
    }
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    if (framesPerBurst <= 0) {
        framesPerBurst = 192;
    }
    
    int32_t desiredBufferSize = framesPerBurst * 4;
    int32_t maxBufferSize = m_stream->getBufferCapacityInFrames();
    
    if (desiredBufferSize > maxBufferSize) {
        desiredBufferSize = maxBufferSize;
    }
    
    auto result = m_stream->setBufferSizeInFrames(desiredBufferSize);
    return result == oboe::Result::OK;
}

bool OboeAudioRenderer::OpenStream() {
    return ConfigureAndOpenStream();
}

void OboeAudioRenderer::CloseStream() {
    if (m_stream) {
        if (m_stream_active.load()) {
            m_stream->stop();
        }
        m_stream->close();
        m_stream.reset();
        m_stream_active.store(false);
    }
}

bool OboeAudioRenderer::TryRecoverStream() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (!m_initialized.load()) {
        return false;
    }
    
    CloseStream();
    ClearAllBuffers();
    
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    
    bool success = ConfigureAndOpenStream();
    
    if (success) {
        m_recovery_pending.store(false);
    }
    
    return success;
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
        return false;
    }
    
    if (m_recovery_pending.load()) {
        if (!TryRecoverStream()) {
            return false;
        }
    }
    
    if (sampleFormat != m_sample_format.load()) {
        return false;
    }
    
    int32_t channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t total_bytes = num_frames * channels * bytes_per_sample;
    size_t frames_written = 0;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    
    while (frames_written < num_frames) {
        auto block = AcquireBlock();
        if (!block) {
            return false;
        }
        
        size_t bytes_remaining = total_bytes - (frames_written * channels * bytes_per_sample);
        size_t copy_size = bytes_remaining < AudioBlock::BLOCK_SIZE ? bytes_remaining : AudioBlock::BLOCK_SIZE;
        
        if (copy_size > AudioBlock::BLOCK_SIZE) {
            ReleaseBlock(std::move(block));
            return false;
        }
        
        std::memcpy(block->data, byte_data + (frames_written * channels * bytes_per_sample), copy_size);
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue->push(std::move(block))) {
            return false;
        }
        
        size_t frames_in_block = copy_size / (channels * bytes_per_sample);
        frames_written += frames_in_block;
    }
    
    return true;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load() || !m_stream_active.load()) {
        return 0;
    }
    
    int32_t total_frames = 0;
    int32_t device_channels = m_device_channels;
    
    if (m_current_block && !m_current_block->consumed) {
        size_t bytes_remaining = m_current_block->available();
        size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        if (device_channels > 0 && bytes_per_sample > 0) {
            total_frames += static_cast<int32_t>(bytes_remaining / (device_channels * bytes_per_sample));
        }
    }
    
    uint32_t queue_size = m_audio_queue->size();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    
    if (device_channels > 0 && bytes_per_sample > 0) {
        int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / (device_channels * bytes_per_sample));
        total_frames += queue_size * frames_per_block;
    }
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    volume = volume < 0.0f ? 0.0f : (volume > 1.0f ? 1.0f : volume);
    m_target_volume = volume;
    m_volume.store(volume);
}

void OboeAudioRenderer::ApplyVolume(void* audioData, int32_t num_frames, int32_t format) {
    if (m_current_volume == m_target_volume && m_current_volume == 1.0f) {
        return;
    }
    
    float target = m_target_volume;
    if (fabsf(m_current_volume - target) > VOLUME_RAMP_SPEED) {
        if (m_current_volume < target) {
            m_current_volume = (m_current_volume + VOLUME_RAMP_SPEED < target) ? 
                              m_current_volume + VOLUME_RAMP_SPEED : target;
        } else {
            m_current_volume = (m_current_volume - VOLUME_RAMP_SPEED > target) ? 
                              m_current_volume - VOLUME_RAMP_SPEED : target;
        }
    } else {
        m_current_volume = target;
    }
    
    if (m_current_volume == 1.0f) {
        return;
    }
    
    if (format == PCM_INT16) {
        int16_t* samples = static_cast<int16_t*>(audioData);
        int32_t num_samples = num_frames * m_device_channels;
        for (int32_t i = 0; i < num_samples; ++i) {
            samples[i] = static_cast<int16_t>(samples[i] * m_current_volume);
        }
    } else if (format == PCM_FLOAT) {
        float* samples = static_cast<float*>(audioData);
        int32_t num_samples = num_frames * m_device_channels;
        for (int32_t i = 0; i < num_samples; ++i) {
            samples[i] *= m_current_volume;
        }
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
    int32_t device_channels = m_device_channels;
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_needed = num_frames * device_channels * bytes_per_sample;
    
    memset(audioData, 0, bytes_needed);
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_copied = 0;
    int32_t frames_copied = 0;
    
    while (frames_copied < num_frames) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                ReleaseBlock(std::move(m_current_block));
                m_current_block.reset();
            }
            
            if (!m_audio_queue->pop(m_current_block)) {
                break;
            }
        }
        
        if (m_current_block->sample_format != m_sample_format.load()) {
            ReleaseBlock(std::move(m_current_block));
            m_current_block.reset();
            continue;
        }
        
        size_t bytes_available = m_current_block->available();
        size_t bytes_to_copy = bytes_available < (bytes_needed - bytes_copied) ? 
                              bytes_available : (bytes_needed - bytes_copied);
        
        if (bytes_to_copy == 0) {
            break;
        }
        
        memcpy(output + bytes_copied, 
               m_current_block->data + m_current_block->data_played,
               bytes_to_copy);
        
        bytes_copied += bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        frames_copied = static_cast<int32_t>(bytes_copied / (device_channels * bytes_per_sample));
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    ApplyVolume(audioData, frames_copied, m_sample_format.load());
}

void OboeAudioRenderer::OnErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_stream_active.store(false);
    m_recovery_pending.store(true);
}

void OboeAudioRenderer::OnErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_stream_active.store(false);
    m_recovery_pending.store(true);
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    ClearAllBuffers();
    
    if (m_initialized.load()) {
        CloseStream();
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