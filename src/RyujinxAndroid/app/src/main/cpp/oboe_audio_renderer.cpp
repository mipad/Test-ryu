// oboe_audio_renderer.cpp (支持原始格式)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>
#include <array>

namespace RyujinxOboe {

// =============== SampleBufferQueue Implementation ===============
bool OboeAudioRenderer::SampleBufferQueue::Write(const int16_t* samples, size_t sample_count) {
    if (!samples || sample_count == 0) return false;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 检查队列是否已满
    if (m_buffers.size() >= m_max_buffers) {
        return false;
    }
    
    // 创建新样本缓冲区
    SampleBuffer buffer;
    buffer.samples.resize(sample_count);
    std::memcpy(buffer.samples.data(), samples, sample_count * sizeof(int16_t));
    buffer.sample_count = sample_count;
    buffer.samples_played = 0;
    buffer.consumed = false;
    
    m_buffers.push(std::move(buffer));
    return true;
}

size_t OboeAudioRenderer::SampleBufferQueue::Read(int16_t* output, size_t samples_requested) {
    if (!output || samples_requested == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t samples_written = 0;
    
    while (samples_written < samples_requested) {
        // 如果当前播放缓冲区已消费或为空，从队列获取新缓冲区
        if (m_playing_buffer.consumed || m_playing_buffer.samples.empty()) {
            if (m_buffers.empty()) {
                break; // 没有更多数据
            }
            
            m_playing_buffer = std::move(m_buffers.front());
            m_buffers.pop();
        }
        
        // 计算当前缓冲区可用的样本
        size_t samples_available = m_playing_buffer.sample_count - m_playing_buffer.samples_played;
        size_t samples_to_copy = std::min(samples_available, samples_requested - samples_written);
        
        // 复制样本到输出
        std::memcpy(output + samples_written, 
                   m_playing_buffer.samples.data() + m_playing_buffer.samples_played,
                   samples_to_copy * sizeof(int16_t));
        
        samples_written += samples_to_copy;
        m_playing_buffer.samples_played += samples_to_copy;
        
        // 检查当前缓冲区是否已完全消费
        if (m_playing_buffer.samples_played >= m_playing_buffer.sample_count) {
            m_playing_buffer.consumed = true;
        }
    }
    
    return samples_written;
}

size_t OboeAudioRenderer::SampleBufferQueue::Available() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t total_samples = 0;
    
    // 计算队列中所有缓冲区的总样本数
    std::queue<SampleBuffer> temp_queue = m_buffers;
    while (!temp_queue.empty()) {
        const auto& buffer = temp_queue.front();
        total_samples += buffer.sample_count;
        temp_queue.pop();
    }
    
    // 加上当前播放缓冲区剩余的样本数
    if (!m_playing_buffer.consumed && !m_playing_buffer.samples.empty()) {
        total_samples += (m_playing_buffer.sample_count - m_playing_buffer.samples_played);
    }
    
    return total_samples;
}

void OboeAudioRenderer::SampleBufferQueue::Clear() {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    while (!m_buffers.empty()) {
        m_buffers.pop();
    }
    
    m_playing_buffer = SampleBuffer{};
}

// =============== RawSampleBufferQueue Implementation ===============
bool OboeAudioRenderer::RawSampleBufferQueue::WriteRaw(const uint8_t* data, size_t data_size, int32_t sample_format) {
    if (!data || data_size == 0) return false;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 检查队列是否已满
    if (m_buffers.size() >= m_max_buffers) {
        return false;
    }
    
    // 创建新原始数据缓冲区
    RawSampleBuffer buffer;
    buffer.data.resize(data_size);
    std::memcpy(buffer.data.data(), data, data_size);
    buffer.data_size = data_size;
    buffer.data_played = 0;
    buffer.sample_format = sample_format;
    buffer.consumed = false;
    
    m_buffers.push(std::move(buffer));
    m_current_format = sample_format;
    
    return true;
}

size_t OboeAudioRenderer::RawSampleBufferQueue::ReadRaw(uint8_t* output, size_t output_size, int32_t target_format) {
    if (!output || output_size == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t bytes_written = 0;
    
    while (bytes_written < output_size) {
        // 如果当前播放缓冲区已消费或为空，从队列获取新缓冲区
        if (m_playing_buffer.consumed || m_playing_buffer.data.empty()) {
            if (m_buffers.empty()) {
                break; // 没有更多数据
            }
            
            m_playing_buffer = std::move(m_buffers.front());
            m_buffers.pop();
        }
        
        // 计算当前缓冲区可用的数据
        size_t bytes_available = m_playing_buffer.data_size - m_playing_buffer.data_played;
        size_t bytes_to_copy = std::min(bytes_available, output_size - bytes_written);
        
        // 复制数据到输出
        std::memcpy(output + bytes_written, 
                   m_playing_buffer.data.data() + m_playing_buffer.data_played,
                   bytes_to_copy);
        
        bytes_written += bytes_to_copy;
        m_playing_buffer.data_played += bytes_to_copy;
        
        // 检查当前缓冲区是否已完全消费
        if (m_playing_buffer.data_played >= m_playing_buffer.data_size) {
            m_playing_buffer.consumed = true;
        }
    }
    
    return bytes_written;
}

size_t OboeAudioRenderer::RawSampleBufferQueue::Available() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t total_bytes = 0;
    
    // 计算队列中所有缓冲区的总字节数
    std::queue<RawSampleBuffer> temp_queue = m_buffers;
    while (!temp_queue.empty()) {
        const auto& buffer = temp_queue.front();
        total_bytes += buffer.data_size;
        temp_queue.pop();
    }
    
    // 加上当前播放缓冲区剩余的字节数
    if (!m_playing_buffer.consumed && !m_playing_buffer.data.empty()) {
        total_bytes += (m_playing_buffer.data_size - m_playing_buffer.data_played);
    }
    
    return total_bytes;
}

void OboeAudioRenderer::RawSampleBufferQueue::Clear() {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    while (!m_buffers.empty()) {
        m_buffers.pop();
    }
    
    m_playing_buffer = RawSampleBuffer{};
}

// =============== Audio Callback Implementation ===============
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

// =============== OboeAudioRenderer Implementation ===============
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
    m_current_sample_format = GetFormatName(sampleFormat);
    
    // 使用原始格式样本缓冲区队列
    m_raw_sample_queue = std::make_unique<RawSampleBufferQueue>(32);
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_sample_queue) {
        m_sample_queue->Clear();
        m_sample_queue.reset();
    }
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
        m_raw_sample_queue.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    // AAudio 独占模式配置 - 兼容Oboe 1.10
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)  // 独占模式
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium) // 中等质量，更好的性能
           ->setFormat(m_oboe_format)  // 使用请求的格式
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game);
    
    // 在Oboe 1.10中，ContentType可能不可用，所以移除这行
    // ->setContentType(oboe::ContentType::Game);
    
    // 设置固定的回调帧数，而不是使用FramesPerCallback枚举
    builder.setFramesPerCallback(240); // 使用固定值
    
    // 设置声道配置
    auto channel_count = m_channel_count.load();
    auto channel_mask = [&]() {
        switch (channel_count) {
        case 1:
            return oboe::ChannelMask::Mono;
        case 2:
            return oboe::ChannelMask::Stereo;
        case 6:
            return oboe::ChannelMask::CM5Point1;
        default:
            return oboe::ChannelMask::Unspecified;
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
    
    // 尝试AAudio独占模式
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        // 回退到AAudio共享模式
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            // 最终回退到OpenSLES
            builder.setAudioApi(oboe::AudioApi::OpenSLES)
                   ->setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
            
            if (result != oboe::Result::OK) {
                return false;
            } else {
                m_current_audio_api = "OpenSLES";
                m_current_sharing_mode = "Shared";
            }
        } else {
            m_current_audio_api = "AAudio";
            m_current_sharing_mode = "Shared";
        }
    } else {
        m_current_audio_api = "AAudio";
        m_current_sharing_mode = "Exclusive";
    }
    
    // 优化缓冲区大小
    int32_t desired_buffer_size = TARGET_SAMPLE_COUNT * 4; // 更大的缓冲区减少underrun
    auto setBufferResult = m_stream->setBufferSizeInFrames(desired_buffer_size);
    
    m_device_channels = m_stream->getChannelCount();
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
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
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    // 计算总样本数
    int32_t system_channels = m_channel_count.load();
    size_t total_samples = num_frames * system_channels;
    size_t data_size = total_samples * sizeof(int16_t);
    
    // 转换为原始格式写入
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    if (!m_raw_sample_queue) {
        return false;
    }
    
    // 计算数据大小
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t data_size = num_frames * system_channels * bytes_per_sample;
    
    // 直接写入原始数据
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    bool success = m_raw_sample_queue->WriteRaw(byte_data, data_size, sampleFormat);
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_raw_sample_queue) return 0;
    
    size_t total_bytes = m_raw_sample_queue->Available();
    int32_t device_channels = m_device_channels;
    int32_t current_format = m_raw_sample_queue->GetCurrentFormat();
    size_t bytes_per_sample = GetBytesPerSample(current_format);
    
    if (device_channels == 0 || bytes_per_sample == 0) {
        return 0;
    }
    
    // 将字节数转换为帧数
    return static_cast<int32_t>(total_bytes / (device_channels * bytes_per_sample));
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
    }
    
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT8:   return oboe::AudioFormat::I8;
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        default:         return oboe::AudioFormat::I16; // 默认回退
    }
}

const char* OboeAudioRenderer::GetFormatName(int32_t format) {
    switch (format) {
        case PCM_INT8:   return "PCM8";
        case PCM_INT16:  return "PCM16";
        case PCM_INT24:  return "PCM24";
        case PCM_INT32:  return "PCM32";
        case PCM_FLOAT:  return "Float32";
        default:         return "Unknown";
    }
}

size_t OboeAudioRenderer::GetBytesPerSample(int32_t format) {
    switch (format) {
        case PCM_INT8:   return 1;
        case PCM_INT16:  return 2;
        case PCM_INT24:  return 3;
        case PCM_INT32:  return 4;
        case PCM_FLOAT:  return 4;
        default:         return 2; // 默认PCM16
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_sample_queue) {
        int32_t channels = m_device_channels;
        std::memset(audioData, 0, num_frames * channels * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }
    
    int16_t* output = static_cast<int16_t*>(audioData);
    int32_t channels = m_device_channels;
    size_t samples_requested = num_frames * channels;
    
    // 从样本队列读取数据
    size_t samples_read = m_sample_queue->Read(output, samples_requested);
    
    // 如果数据不足，填充静音
    if (samples_read < samples_requested) {
        size_t samples_remaining = samples_requested - samples_read;
        std::memset(output + samples_read, 0, samples_remaining * sizeof(int16_t));
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_raw_sample_queue) {
        int32_t channels = m_device_channels;
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        std::memset(audioData, 0, num_frames * channels * bytes_per_sample);
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t channels = m_device_channels;
    size_t target_bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_requested = num_frames * channels * target_bytes_per_sample;
    
    // 从原始样本队列读取数据
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_read = m_raw_sample_queue->ReadRaw(output, bytes_requested, m_sample_format.load());
    
    // 如果数据不足，填充静音
    if (bytes_read < bytes_requested) {
        size_t bytes_remaining = bytes_requested - bytes_read;
        std::memset(output + bytes_read, 0, bytes_remaining);
    }
    
    m_frames_played += num_frames;
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

OboeAudioRenderer::PerformanceStats OboeAudioRenderer::GetStats() const {
    PerformanceStats stats;
    stats.frames_written = m_frames_written.load();
    stats.frames_played = m_frames_played.load();
    stats.underrun_count = m_underrun_count.load();
    stats.stream_restart_count = m_stream_restart_count.load();
    stats.audio_api = m_current_audio_api;
    stats.sharing_mode = m_current_sharing_mode;
    stats.sample_format = m_current_sample_format;
    return stats;
}

} // namespace RyujinxOboe