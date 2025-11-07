// oboe_audio_renderer.cpp (修复版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

// =============== SimpleBufferQueue Implementation ===============
bool OboeAudioRenderer::SimpleBufferQueue::Write(const int16_t* data, size_t frames, int32_t channels) {
    if (!data || frames == 0) return false;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 检查队列是否已满
    if (m_buffers.size() >= m_max_buffers) {
        return false;
    }
    
    // 创建新缓冲区
    AudioBuffer buffer;
    size_t samples = frames * channels;
    buffer.data.resize(samples);
    std::memcpy(buffer.data.data(), data, samples * sizeof(int16_t));
    buffer.frames = frames;
    buffer.frames_played = 0;
    buffer.consumed = false;
    
    m_buffers.push(std::move(buffer));
    return true;
}

size_t OboeAudioRenderer::SimpleBufferQueue::Read(int16_t* output, size_t frames, int32_t channels) {
    if (!output || frames == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t frames_written = 0;
    size_t samples_needed = frames * channels;
    size_t samples_written = 0;
    
    while (samples_written < samples_needed) {
        // 如果当前播放缓冲区已消费或为空，从队列获取新缓冲区
        if (m_playing_buffer.consumed || m_playing_buffer.data.empty()) {
            if (m_buffers.empty()) {
                break; // 没有更多数据
            }
            
            m_playing_buffer = std::move(m_buffers.front());
            m_buffers.pop();
        }
        
        // 计算当前缓冲区可用的样本
        size_t remaining_frames = m_playing_buffer.frames - m_playing_buffer.frames_played;
        size_t frames_to_copy = std::min(remaining_frames, frames - frames_written);
        size_t samples_to_copy = frames_to_copy * channels;
        
        // 复制数据到输出
        std::memcpy(output + samples_written, 
                   m_playing_buffer.data.data() + (m_playing_buffer.frames_played * channels),
                   samples_to_copy * sizeof(int16_t));
        
        samples_written += samples_to_copy;
        frames_written += frames_to_copy;
        m_playing_buffer.frames_played += frames_to_copy;
        
        // 检查当前缓冲区是否已完全消费
        if (m_playing_buffer.frames_played >= m_playing_buffer.frames) {
            m_playing_buffer.consumed = true;
        }
    }
    
    return frames_written;
}

size_t OboeAudioRenderer::SimpleBufferQueue::Available() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t total_frames = 0;
    
    // 计算队列中所有缓冲区的总帧数
    std::queue<AudioBuffer> temp_queue = m_buffers;
    while (!temp_queue.empty()) {
        const auto& buffer = temp_queue.front();
        total_frames += buffer.frames;
        temp_queue.pop();
    }
    
    // 加上当前播放缓冲区剩余的帧数
    if (!m_playing_buffer.consumed && !m_playing_buffer.data.empty()) {
        total_frames += (m_playing_buffer.frames - m_playing_buffer.frames_played);
    }
    
    return total_frames;
}

void OboeAudioRenderer::SimpleBufferQueue::Clear() {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    while (!m_buffers.empty()) {
        m_buffers.pop();
    }
    
    m_playing_buffer = AudioBuffer{};
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::YuzuStyleAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::YuzuStyleErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Audio stream closed with error: %d", error);
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::YuzuStyleErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error before close: %d", error);
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<YuzuStyleAudioCallback>(this);
    m_error_callback = std::make_unique<YuzuStyleErrorCallback>(this);
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
    
    LOGI("Initializing Oboe (Yuzu-style): %dHz %dch", sampleRate, channelCount);
    
    // 使用简单的缓冲区队列，基于yuzu设计
    m_buffer_queue = std::make_unique<SimpleBufferQueue>(32); // 32个缓冲区，与yuzu相同
    
    if (!ConfigureAndOpenStream()) {
        LOGE("Failed to open Yuzu-style audio stream");
        return false;
    }
    
    m_initialized.store(true);
    LOGI("OboeAudioRenderer Yuzu-style initialization complete");
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_buffer_queue) {
        m_buffer_queue->Clear();
        m_buffer_queue.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
    LOGI("OboeAudioRenderer shutdown");
}

void OboeAudioRenderer::ConfigureForYuzuStyle(oboe::AudioStreamBuilder& builder) {
    // 完全基于yuzu的配置
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::OpenSLES)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(TARGET_SAMPLE_RATE)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
           ->setFormat(oboe::AudioFormat::I16)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setBufferCapacityInFrames(TARGET_SAMPLE_COUNT * 2);
    
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
    
    ConfigureForYuzuStyle(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 直接使用OpenSLES，与yuzu完全一致
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        LOGE("OpenSLES stream open failed: %d", result);
        return false;
    }
    
    // 设置缓冲区大小
    auto setBufferResult = m_stream->setBufferSizeInFrames(TARGET_SAMPLE_COUNT * 2);
    if (setBufferResult) {
        LOGD("Buffer size set to %d frames", setBufferResult.value());
    }
    
    device_channels = m_stream->getChannelCount();
    const auto sample_rate = m_stream->getSampleRate();
    const auto buffer_capacity = m_stream->getBufferCapacityInFrames();
    const auto stream_backend = 
        m_stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES";

    LOGI("Opened %s stream with %d channels, %d Hz, capacity %d frames",
         stream_backend, device_channels, sample_rate, buffer_capacity);
    
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
    
    if (!m_buffer_queue) {
        return false;
    }
    
    // 应用音量
    float volume = m_volume.load();
    bool apply_volume = (volume != 1.0f);
    bool success;
    
    if (apply_volume) {
        std::vector<int16_t> volume_adjusted(num_frames * m_channel_count.load());
        for (int32_t i = 0; i < num_frames * m_channel_count.load(); i++) {
            volume_adjusted[i] = static_cast<int16_t>(data[i] * volume);
        }
        success = m_buffer_queue->Write(volume_adjusted.data(), num_frames, m_channel_count.load());
    } else {
        success = m_buffer_queue->Write(data, num_frames, m_channel_count.load());
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
        LOGW("Audio buffer queue full: %d frames dropped", num_frames);
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return m_buffer_queue ? static_cast<int32_t>(m_buffer_queue->Available()) : 0;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_buffer_queue) {
        m_buffer_queue->Clear();
    }
    
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
    LOGI("Audio stream reset (count: %d)", m_stream_restart_count.load());
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_buffer_queue) {
        int32_t channels = m_channel_count.load();
        std::memset(audioData, 0, num_frames * channels * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }
    
    int16_t* output = static_cast<int16_t*>(audioData);
    int32_t channels = m_channel_count.load();
    
    // 从缓冲区队列读取数据
    size_t frames_read = m_buffer_queue->Read(output, num_frames, channels);
    
    // 如果数据不足，填充静音
    if (frames_read < static_cast<size_t>(num_frames)) {
        size_t samples_remaining = (num_frames - frames_read) * channels;
        std::memset(output + (frames_read * channels), 0, samples_remaining * sizeof(int16_t));
        
        static int underflow_log_counter = 0;
        if (++underflow_log_counter >= 10) {
            LOGW("Audio underflow: %zu/%d frames available", frames_read, num_frames);
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
    return stats;
}

} // namespace RyujinxOboe