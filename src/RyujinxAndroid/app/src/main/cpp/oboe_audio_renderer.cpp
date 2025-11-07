// oboe_audio_renderer.cpp (修复回调函数版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

namespace RyujinxOboe {

// =============== SimpleRingBuffer Implementation ===============
OboeAudioRenderer::SimpleRingBuffer::SimpleRingBuffer(size_t capacity) 
    : m_capacity(capacity), m_buffer(capacity * 2) { // 假设2声道
    LOGI("SimpleRingBuffer created with capacity: %zu frames", capacity);
}

OboeAudioRenderer::SimpleRingBuffer::~SimpleRingBuffer() {
    Clear();
}

bool OboeAudioRenderer::SimpleRingBuffer::Write(const int16_t* data, size_t frames, int32_t channels) {
    if (!data || frames == 0) return false;
    
    size_t samples_needed = frames * channels;
    size_t write_pos = m_write_pos.load();
    size_t read_pos = m_read_pos.load();
    
    // 计算可用空间
    size_t available_space;
    if (write_pos >= read_pos) {
        available_space = m_buffer.size() - write_pos + read_pos;
    } else {
        available_space = read_pos - write_pos;
    }
    
    // 保留一点空间避免完全填满
    if (available_space <= samples_needed + channels) {
        LOGW("RingBuffer overflow: needed %zu, available %zu", samples_needed, available_space);
        return false;
    }
    
    // 写入数据
    size_t end_pos = write_pos + samples_needed;
    if (end_pos <= m_buffer.size()) {
        std::memcpy(&m_buffer[write_pos], data, samples_needed * sizeof(int16_t));
    } else {
        size_t first_part = m_buffer.size() - write_pos;
        std::memcpy(&m_buffer[write_pos], data, first_part * sizeof(int16_t));
        std::memcpy(&m_buffer[0], data + first_part, (samples_needed - first_part) * sizeof(int16_t));
    }
    
    m_write_pos.store((write_pos + samples_needed) % m_buffer.size());
    return true;
}

size_t OboeAudioRenderer::SimpleRingBuffer::Read(int16_t* output, size_t frames, int32_t channels) {
    if (!output || frames == 0) return 0;
    
    size_t samples_requested = frames * channels;
    size_t write_pos = m_write_pos.load();
    size_t read_pos = m_read_pos.load();
    
    // 计算可用数据
    size_t available_data;
    if (write_pos >= read_pos) {
        available_data = write_pos - read_pos;
    } else {
        available_data = m_buffer.size() - read_pos + write_pos;
    }
    
    size_t samples_to_read = std::min(samples_requested, available_data);
    if (samples_to_read == 0) {
        return 0;
    }
    
    // 读取数据
    size_t end_pos = read_pos + samples_to_read;
    if (end_pos <= m_buffer.size()) {
        std::memcpy(output, &m_buffer[read_pos], samples_to_read * sizeof(int16_t));
    } else {
        size_t first_part = m_buffer.size() - read_pos;
        std::memcpy(output, &m_buffer[read_pos], first_part * sizeof(int16_t));
        std::memcpy(output + first_part, &m_buffer[0], (samples_to_read - first_part) * sizeof(int16_t));
    }
    
    m_read_pos.store((read_pos + samples_to_read) % m_buffer.size());
    return samples_to_read / channels;
}

size_t OboeAudioRenderer::SimpleRingBuffer::Available() const {
    size_t write_pos = m_write_pos.load();
    size_t read_pos = m_read_pos.load();
    
    if (write_pos >= read_pos) {
        return (write_pos - read_pos) / m_channels;
    } else {
        return (m_buffer.size() - read_pos + write_pos) / m_channels;
    }
}

void OboeAudioRenderer::SimpleRingBuffer::Clear() {
    m_read_pos.store(0);
    m_write_pos.store(0);
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::SimpleAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::SimpleErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Audio stream closed with error: %d", error);
    m_renderer->OnStreamError(error);
}

void OboeAudioRenderer::SimpleErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error before close: %d", error);
    m_renderer->OnStreamError(error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<SimpleAudioCallback>(this);
    m_error_callback = std::make_unique<SimpleErrorCallback>(this);
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
        return true;
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    
    // 计算缓冲区大小
    size_t buffer_capacity = (sampleRate * BUFFER_DURATION_MS) / 1000;
    m_ring_buffer = std::make_unique<SimpleRingBuffer>(buffer_capacity);
    
    if (!OpenStream()) {
        LOGE("Failed to open audio stream");
        return false;
    }
    
    m_initialized.store(true);
    LOGI("OboeAudioRenderer initialized: %d Hz, %d channels", sampleRate, channelCount);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
        m_ring_buffer.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
    LOGI("OboeAudioRenderer shutdown");
}

bool OboeAudioRenderer::OpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 使用最保守的设置
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Shared) // 共享模式避免冲突
           ->setFormat(oboe::AudioFormat::I16) // 直接使用16位整数
           ->setChannelCount(m_channel_count.load())
           ->setSampleRate(m_sample_rate.load())
           ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK)
           ->setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 首先尝试AAudio
    builder.setAudioApi(oboe::AudioApi::AAudio);
    auto result = builder.openStream(m_stream);
    
    // 如果AAudio失败，尝试OpenSLES
    if (result != oboe::Result::OK) {
        LOGW("AAudio failed, trying OpenSLES");
        builder.setAudioApi(oboe::AudioApi::OpenSLES);
        result = builder.openStream(m_stream);
    }
    
    if (result != oboe::Result::OK) {
        LOGE("Failed to open audio stream with both APIs: %d", result);
        return false;
    }
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start audio stream: %d", result);
        m_stream->close();
        m_stream.reset();
        return false;
    }
    
    m_stream_started.store(true);
    
    LOGI("Audio stream opened: %s, %d channels, %d Hz", 
         m_stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
         m_stream->getChannelCount(), m_stream->getSampleRate());
    
    return true;
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
    
    if (!m_ring_buffer) {
        return false;
    }
    
    bool success = m_ring_buffer->Write(data, num_frames, m_channel_count.load());
    if (success) {
        m_frames_written += num_frames;
    } else {
        LOGW("Failed to write %d frames to ring buffer", num_frames);
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return m_ring_buffer ? static_cast<int32_t>(m_ring_buffer->Available()) : 0;
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
    }
    
    // 重新打开流
    CloseStream();
    OpenStream();
    
    LOGI("Audio stream reset");
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_ring_buffer) {
        // 输出静音
        std::memset(audioData, 0, num_frames * m_channel_count.load() * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }
    
    int16_t* output = static_cast<int16_t*>(audioData);
    int32_t channels = m_channel_count.load();
    float volume = m_volume.load();
    
    // 从环形缓冲区读取数据
    size_t frames_read = m_ring_buffer->Read(output, num_frames, channels);
    
    // 如果数据不足，填充静音
    if (frames_read < static_cast<size_t>(num_frames)) {
        size_t samples_remaining = (num_frames - frames_read) * channels;
        std::memset(output + (frames_read * channels), 0, samples_remaining * sizeof(int16_t));
        
        if (frames_read == 0) {
            LOGW("Audio underrun: no data available for %d frames", num_frames);
        }
    }
    
    // 应用音量
    if (volume != 1.0f) {
        size_t total_samples = num_frames * channels;
        for (size_t i = 0; i < total_samples; i++) {
            output[i] = static_cast<int16_t>(output[i] * volume);
        }
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::Result error) {
    LOGW("Stream error occurred: %d", error);
    
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false);
    
    // 尝试恢复流
    if (m_initialized.load()) {
        LOGI("Attempting to recover audio stream");
        CloseStream();
        OpenStream();
    }
}

} // namespace RyujinxOboe