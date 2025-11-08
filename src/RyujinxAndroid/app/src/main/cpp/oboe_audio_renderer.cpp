// oboe_audio_renderer.cpp (兼容Oboe 1.10版本)
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

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("AAudio stream closed with error: %d", error);
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("AAudio stream error before close: %d", error);
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
            LOGI("Audio parameters changed: %dHz %dch -> %dHz %dch", 
                 m_sample_rate.load(), m_channel_count.load(), sampleRate, channelCount);
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    
    LOGI("Initializing AAudio (Exclusive Mode): %dHz %dch", sampleRate, channelCount);
    
    // 使用样本缓冲区队列
    m_sample_queue = std::make_unique<SampleBufferQueue>(32);
    
    if (!ConfigureAndOpenStream()) {
        LOGE("Failed to open AAudio exclusive stream");
        return false;
    }
    
    m_initialized.store(true);
    LOGI("AAudio exclusive initialization complete");
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
    LOGI("OboeAudioRenderer shutdown");
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    // AAudio 独占模式配置 - 兼容Oboe 1.10
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)  // 独占模式
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium) // 中等质量，更好的性能
           ->setFormat(oboe::AudioFormat::I16)
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
        LOGW("AAudio exclusive failed, trying AAudio shared mode");
        
        // 回退到AAudio共享模式
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            LOGW("AAudio shared failed, trying OpenSLES");
            
            // 最终回退到OpenSLES
            builder.setAudioApi(oboe::AudioApi::OpenSLES)
                   ->setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
            
            if (result != oboe::Result::OK) {
                LOGE("All audio API attempts failed");
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
    if (setBufferResult) {
        LOGD("Buffer size set to %d frames", setBufferResult.value());
    }
    
    m_device_channels = m_stream->getChannelCount();
    const auto sample_rate = m_stream->getSampleRate();
    const auto buffer_capacity = m_stream->getBufferCapacityInFrames();
    const auto actual_buffer_size = m_stream->getBufferSizeInFrames();
    const auto frames_per_callback = m_stream->getFramesPerCallback();

    LOGI("Opened %s %s stream: %d channels, %d Hz, buffer: %d/%d frames, %d frames/callback",
         m_current_audio_api.c_str(), m_current_sharing_mode.c_str(),
         m_device_channels, sample_rate, actual_buffer_size, buffer_capacity, frames_per_callback);
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start audio stream: %d", result);
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
                
                // 应用下混系数 (基于标准下混算法)
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
        LOGW("Audio sample queue full: %d frames (%zu samples) dropped", 
             num_frames, total_samples);
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

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_sample_queue) {
        m_sample_queue->Clear();
    }
    
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
    LOGI("Audio stream reset (count: %d)", m_stream_restart_count.load());
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
        
        static int underflow_log_counter = 0;
        if (++underflow_log_counter >= 10) {
            LOGW("Audio underflow: %zu/%zu samples available", samples_read, samples_requested);
            underflow_log_counter = 0;
        }
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGI("Audio stream closed, reinitializing");
    
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_initialized.load()) {
        CloseStream();
        if (ConfigureAndOpenStream()) {
            LOGI("Audio stream recovered successfully");
        } else {
            LOGE("Failed to recover audio stream");
        }
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error before close: %d", error);
    
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
    return stats;
}

} // namespace RyujinxOboe
