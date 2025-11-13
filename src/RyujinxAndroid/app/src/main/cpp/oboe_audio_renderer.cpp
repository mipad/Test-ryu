// oboe_audio_renderer.cpp (修复 Usage 配置)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>
#include <array>
#include <limits>

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
    buffer.is_compressed = false;
    
    m_buffers.push(std::move(buffer));
    return true;
}

bool OboeAudioRenderer::SampleBufferQueue::WriteCompressed(const uint8_t* data, size_t data_size, 
                                                         oboe::AudioFormat format, int32_t num_frames) {
    if (!data || data_size == 0) return false;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 检查队列是否已满
    if (m_buffers.size() >= m_max_buffers) {
        return false;
    }
    
    // 创建压缩数据缓冲区
    SampleBuffer buffer;
    buffer.compressed_data.resize(data_size);
    std::memcpy(buffer.compressed_data.data(), data, data_size);
    buffer.sample_count = num_frames; // 帧数而不是样本数
    buffer.samples_played = 0;
    buffer.consumed = false;
    buffer.is_compressed = true;
    buffer.compressed_format = format;
    
    m_buffers.push(std::move(buffer));
    return true;
}

size_t OboeAudioRenderer::SampleBufferQueue::Read(int16_t* output, size_t samples_requested) {
    if (!output || samples_requested == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t samples_written = 0;
    
    while (samples_written < samples_requested) {
        // 如果当前播放缓冲区已消费或为空，从队列获取新缓冲区
        if (m_playing_buffer.consumed || 
            (m_playing_buffer.samples.empty() && m_playing_buffer.compressed_data.empty())) {
            if (m_buffers.empty()) {
                break; // 没有更多数据
            }
            
            m_playing_buffer = std::move(m_buffers.front());
            m_buffers.pop();
        }
        
        // 处理压缩数据 - 注意：在实际实现中，这里需要解码器
        if (m_playing_buffer.is_compressed) {
            // 对于压缩格式，我们无法直接读取，需要填充静音或使用软件解码
            // 这里简化处理：填充静音
            size_t frames_remaining = m_playing_buffer.sample_count - m_playing_buffer.samples_played;
            size_t frames_to_fill = std::min(frames_remaining, 
                                           samples_requested - samples_written);
            
            // 填充静音
            std::memset(output + samples_written, 0, frames_to_fill * sizeof(int16_t));
            
            samples_written += frames_to_fill;
            m_playing_buffer.samples_played += frames_to_fill;
        } else {
            // 处理PCM数据
            size_t samples_available = m_playing_buffer.sample_count - m_playing_buffer.samples_played;
            size_t samples_to_copy = std::min(samples_available, samples_requested - samples_written);
            
            // 复制样本到输出
            std::memcpy(output + samples_written, 
                       m_playing_buffer.samples.data() + m_playing_buffer.samples_played,
                       samples_to_copy * sizeof(int16_t));
            
            samples_written += samples_to_copy;
            m_playing_buffer.samples_played += samples_to_copy;
        }
        
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
    if (!m_playing_buffer.consumed && 
        (!m_playing_buffer.samples.empty() || !m_playing_buffer.compressed_data.empty())) {
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

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
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
    if (m_initialized.load()) {
        if (m_sample_rate.load() != sampleRate || m_channel_count.load() != channelCount) {
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    
    // 使用样本缓冲区队列
    m_sample_queue = std::make_unique<SampleBufferQueue>(32);
    
    // 首先尝试PCM offload模式
    if (m_pcm_offload_enabled.load() && TryOpenOffloadStream()) {
        m_current_audio_format = "PCM Offload";
    } else {
        // 回退到普通模式
        if (!ConfigureAndOpenStream()) {
            return false;
        }
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
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(oboe::AudioFormat::I16)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game);  // 改为 Game usage，适合游戏音频
    
    // 设置固定的回调帧数
    builder.setFramesPerCallback(240);
    
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

bool OboeAudioRenderer::TryOpenOffloadStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置PCM offload流
    builder.setPerformanceMode(oboe::PerformanceMode::None) // Offload使用None性能模式
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Shared) // Offload通常使用共享模式
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setFormat(oboe::AudioFormat::I16)
           ->setUsage(oboe::Usage::Media) // Offload 保持 Media usage，适合长时间播放
           ->setContentType(oboe::ContentType::Music)
           ->setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
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
           ->setChannelMask(channel_mask);
    
    // 尝试开启offload流
    auto result = builder.openStream(m_stream);
    
    if (result == oboe::Result::OK) {
        m_current_audio_api = "AAudio";
        m_current_sharing_mode = "Shared";
        m_current_audio_format = "PCM Offload";
        
        // 启动流
        result = m_stream->requestStart();
        if (result == oboe::Result::OK) {
            m_stream_started.store(true);
            return true;
        }
    }
    
    return false;
}

bool OboeAudioRenderer::TryOpenCompressedStream(oboe::AudioFormat format) {
    oboe::AudioStreamBuilder builder;
    
    // 配置压缩格式流
    builder.setPerformanceMode(oboe::PerformanceMode::None)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setFormat(format) // 设置压缩格式
           ->setUsage(oboe::Usage::Media) // 压缩格式通常使用 Media usage
           ->setContentType(oboe::ContentType::Music)
           ->setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 对于压缩格式，采样率和声道数可能由格式本身决定
    builder.setChannelCount(m_channel_count.load());
    
    auto result = builder.openStream(m_stream);
    
    if (result == oboe::Result::OK) {
        m_current_audio_api = "AAudio";
        m_current_sharing_mode = "Shared";
        m_current_audio_format = "Compressed";
        
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
    int32_t desired_buffer_size = TARGET_SAMPLE_COUNT * 4;
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
    
    if (!m_sample_queue) {
        return false;
    }
    
    // 计算总样本数
    int32_t system_channels = m_channel_count.load();
    int32_t device_channels = m_device_channels;
    size_t total_samples = num_frames * system_channels;
    
    // 应用音量和声道处理
    float volume = m_volume.load();
    bool apply_volume = (volume != 1.0f);
    bool success;
    
    if (apply_volume || system_channels != device_channels) {
        // 需要处理音量或声道转换
        std::vector<int16_t> processed_samples;
        
        if (system_channels == 6 && device_channels == 2) {
            // 6声道到2声道的下混
            processed_samples.resize(num_frames * 2);
            
            // 优化的下混算法
            for (int32_t frame = 0; frame < num_frames; frame++) {
                const int16_t* frame_data = data + (frame * 6);
                
                // 提取各个声道
                float front_left = frame_data[0];
                float front_right = frame_data[1];
                float center = frame_data[2];
                float lfe = frame_data[3];
                float back_left = frame_data[4];
                float back_right = frame_data[5];
                
                // 应用下混系数
                float left = front_left + center * 0.707f + back_left * 0.707f + lfe * 0.5f;
                float right = front_right + center * 0.707f + back_right * 0.707f + lfe * 0.5f;
                
                // 应用音量并限制范围
                constexpr int16_t min_val = -32768;
                constexpr int16_t max_val = 32767;
                
                processed_samples[frame * 2] = static_cast<int16_t>(
                    std::clamp(static_cast<int32_t>(left * volume), 
                              static_cast<int32_t>(min_val), 
                              static_cast<int32_t>(max_val)));
                processed_samples[frame * 2 + 1] = static_cast<int16_t>(
                    std::clamp(static_cast<int32_t>(right * volume), 
                              static_cast<int32_t>(min_val), 
                              static_cast<int32_t>(max_val)));
            }
        } else if (system_channels != device_channels) {
            // 通用的声道处理
            processed_samples.resize(num_frames * device_channels);
            
            int32_t min_channels = std::min(system_channels, device_channels);
            
            for (int32_t frame = 0; frame < num_frames; frame++) {
                for (int32_t ch = 0; ch < min_channels; ch++) {
                    float sample = static_cast<float>(data[frame * system_channels + ch]);
                    processed_samples[frame * device_channels + ch] = static_cast<int16_t>(
                        std::clamp(static_cast<int32_t>(sample * volume), 
                                  static_cast<int32_t>(std::numeric_limits<int16_t>::min()), 
                                  static_cast<int32_t>(std::numeric_limits<int16_t>::max())));
                }
                
                // 对于额外的声道，如果是上混则复制现有声道或填充0
                for (int32_t ch = min_channels; ch < device_channels; ch++) {
                    if (system_channels == 1 && device_channels >= 2) {
                        // 单声道转立体声
                        processed_samples[frame * device_channels + ch] = processed_samples[frame * device_channels];
                    } else {
                        processed_samples[frame * device_channels + ch] = 0;
                    }
                }
            }
        } else {
            // 声道数相同，只应用音量
            processed_samples.resize(total_samples);
            for (size_t i = 0; i < total_samples; ++i) {
                processed_samples[i] = static_cast<int16_t>(data[i] * volume);
            }
        }
        
        success = m_sample_queue->Write(processed_samples.data(), processed_samples.size());
    } else {
        // 直接写入，无音量调整和声道转换
        success = m_sample_queue->Write(data, total_samples);
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
    }
    
    return success;
}

bool OboeAudioRenderer::WriteCompressedAudio(const uint8_t* data, size_t data_size, 
                                           oboe::AudioFormat format, int32_t num_frames) {
    if (!m_initialized.load() || !data || data_size == 0) {
        return false;
    }
    
    if (!m_sample_queue) {
        return false;
    }
    
    // 检查是否支持该压缩格式
    if (!IsCompressedFormatSupported(format)) {
        return false;
    }
    
    // 写入压缩数据
    bool success = m_sample_queue->WriteCompressed(data, data_size, format, num_frames);
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_sample_queue) return 0;
    
    size_t total_samples = m_sample_queue->Available();
    int32_t device_channels = m_device_channels;
    
    // 将样本数转换为帧数
    return device_channels > 0 ? static_cast<int32_t>(total_samples / device_channels) : 0;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::EnablePcmOffload(bool enable) {
    if (m_pcm_offload_enabled.load() != enable) {
        m_pcm_offload_enabled.store(enable);
        
        // 如果已经初始化，需要重新初始化流
        if (m_initialized.load()) {
            Reset();
        }
    }
}

bool OboeAudioRenderer::IsPcmOffloadSupported() const {
    // 检查设备是否支持PCM offload
    // 这里简化实现，实际中应该查询设备能力
    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::None)
           ->setUsage(oboe::Usage::Media)
           ->setContentType(oboe::ContentType::Music);
    
    return builder.isAAudioRecommended();
}

bool OboeAudioRenderer::IsCompressedFormatSupported(oboe::AudioFormat format) const {
    // 检查设备是否支持特定的压缩格式
    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setFormat(format);
    
    // 根据 Oboe 1.10 的实际枚举值进行调整
    switch (format) {
        case oboe::AudioFormat::IEC61937:  // 压缩格式的通用标识
        case oboe::AudioFormat::MP3:
        case oboe::AudioFormat::AAC_LC:    // 使用 AAC_LC 而不是 AAC
            return builder.isAAudioRecommended();
        default:
            return false;
    }
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_sample_queue) {
        m_sample_queue->Clear();
    }
    
    CloseStream();
    
    // 根据当前设置重新初始化流
    if (m_pcm_offload_enabled.load() && TryOpenOffloadStream()) {
        // 使用offload模式
    } else {
        ConfigureAndOpenStream();
    }
    
    m_stream_restart_count++;
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

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_initialized.load()) {
        CloseStream();
        
        // 尝试重新打开流
        if (m_pcm_offload_enabled.load() && TryOpenOffloadStream()) {
            // 使用offload模式
        } else {
            ConfigureAndOpenStream();
        }
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
    stats.pcm_offload_enabled = m_pcm_offload_enabled.load();
    stats.current_audio_format = m_current_audio_format;
    return stats;
}

} // namespace RyujinxOboe
