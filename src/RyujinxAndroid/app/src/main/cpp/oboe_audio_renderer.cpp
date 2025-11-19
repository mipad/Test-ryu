#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <limits>

namespace RyujinxOboe {

// ========== DspProcessor 实现 ==========

void DspProcessor::ProcessAudio(void* data, size_t size, int32_t sample_format, int32_t channels) {
    if (!data || size == 0) return;
    
    float volume = m_volume.load();
    
    // 如果音量为1.0，跳过处理以优化性能
    if (volume >= 0.999f && volume <= 1.001f) {
        return;
    }
    
    switch (sample_format) {
        case PCM_INT16: {
            size_t sample_count = size / sizeof(int16_t);
            ApplyVolumeInt16(static_cast<int16_t*>(data), sample_count, volume);
            break;
        }
        case PCM_INT24: {
            // 24位PCM存储为32位整数，高8位为0
            size_t sample_count = size / sizeof(int32_t);
            ApplyVolumeInt24(static_cast<int32_t*>(data), sample_count, volume);
            break;
        }
        case PCM_INT32: {
            size_t sample_count = size / sizeof(int32_t);
            ApplyVolumeInt32(static_cast<int32_t*>(data), sample_count, volume);
            break;
        }
        case PCM_FLOAT: {
            size_t sample_count = size / sizeof(float);
            ApplyVolumeFloat(static_cast<float*>(data), sample_count, volume);
            break;
        }
        default:
            // 未知格式，不处理
            break;
    }
}

void DspProcessor::ProcessAudioBatch(void* data, size_t frame_count, int32_t sample_format, int32_t channels) {
    if (!data || frame_count == 0) return;
    
    size_t bytes_per_sample = GetBytesPerSample(sample_format);
    size_t total_size = frame_count * channels * bytes_per_sample;
    ProcessAudio(data, total_size, sample_format, channels);
}

void DspProcessor::ApplyVolumeInt16(int16_t* samples, size_t sample_count, float volume) {
    for (size_t i = 0; i < sample_count; ++i) {
        samples[i] = static_cast<int16_t>(ScaleSampleInt16(samples[i], volume));
    }
}

void DspProcessor::ApplyVolumeInt32(int32_t* samples, size_t sample_count, float volume) {
    for (size_t i = 0; i < sample_count; ++i) {
        samples[i] = ScaleSampleInt32(samples[i], volume);
    }
}

void DspProcessor::ApplyVolumeFloat(float* samples, size_t sample_count, float volume) {
    for (size_t i = 0; i < sample_count; ++i) {
        samples[i] = ScaleSampleFloat(samples[i], volume);
    }
}

void DspProcessor::ApplyVolumeInt24(int32_t* samples, size_t sample_count, float volume) {
    // 24位PCM存储为32位，实际数据在低24位
    const int32_t mask_24bit = 0xFFFFFF;
    const int32_t sign_extend = 0xFF000000;
    
    for (size_t i = 0; i < sample_count; ++i) {
        // 提取24位有符号整数
        int32_t sample_24bit = samples[i] & mask_24bit;
        // 符号扩展为32位
        if (sample_24bit & 0x800000) {
            sample_24bit |= sign_extend;
        }
        
        // 应用音量
        int32_t scaled_sample = ScaleSampleInt32(sample_24bit, volume);
        
        // 转换回24位（取低24位）
        samples[i] = scaled_sample & mask_24bit;
    }
}

int32_t DspProcessor::ScaleSampleInt16(int16_t sample, float volume) {
    int32_t scaled = static_cast<int32_t>(sample * volume);
    
    // 饱和处理
    if (scaled > std::numeric_limits<int16_t>::max()) {
        return std::numeric_limits<int16_t>::max();
    } else if (scaled < std::numeric_limits<int16_t>::min()) {
        return std::numeric_limits<int16_t>::min();
    }
    
    return static_cast<int16_t>(scaled);
}

int32_t DspProcessor::ScaleSampleInt32(int32_t sample, float volume) {
    // 使用64位中间值防止溢出
    int64_t scaled = static_cast<int64_t>(sample) * static_cast<int64_t>(volume * 65536.0f);
    scaled /= 65536;
    
    // 饱和处理
    if (scaled > std::numeric_limits<int32_t>::max()) {
        return std::numeric_limits<int32_t>::max();
    } else if (scaled < std::numeric_limits<int32_t>::min()) {
        return std::numeric_limits<int32_t>::min();
    }
    
    return static_cast<int32_t>(scaled);
}

float DspProcessor::ScaleSampleFloat(float sample, float volume) {
    float result = sample * volume;
    
    // 浮点数饱和处理
    if (result > 1.0f) {
        return 1.0f;
    } else if (result < -1.0f) {
        return -1.0f;
    }
    
    return result;
}

// ========== OboeAudioRenderer 实现 ==========

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::GetInstance() {
    static OboeAudioRenderer instance;
    return instance;
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
        
        // 在入队前应用DSP效果
        ApplyDspEffects(block->data, copy_size, sampleFormat);
        
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
    m_dsp_processor.SetVolume(volume);
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_audio_queue.clear();
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

void OboeAudioRenderer::ApplyDspEffects(void* audio_data, size_t data_size, int32_t sample_format) {
    // 目前只应用音量控制
    // 未来可以添加更多DSP效果：重采样、滤波器等
    m_dsp_processor.ProcessAudio(audio_data, data_size, sample_format, m_channel_count.load());
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
                m_object_pool.release(std::move(m_current_block));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 没有数据时填充静音
                std::memset(output + bytes_copied, 0, bytes_remaining);
                break;
            }
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_remaining);
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
    
    // 在最终输出前再次应用DSP效果（确保实时音量控制）
    if (bytes_copied > 0) {
        m_dsp_processor.ProcessAudio(audioData, bytes_copied, m_sample_format.load(), m_device_channels);
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