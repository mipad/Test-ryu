// oboe_audio_renderer.cpp (完整修复版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

namespace RyujinxOboe {

// =============== 设备声道数查询 (基于yuzu实现) ===============
int32_t OboeAudioRenderer::QueryDeviceChannelCount(oboe::Direction direction) {
    std::shared_ptr<oboe::AudioStream> temp_stream;
    oboe::AudioStreamBuilder builder;

    // 配置构建器
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::OpenSLES) // 优先使用OpenSLES避免AAudio问题
           ->setDirection(direction)
           ->setSampleRate(TARGET_SAMPLE_RATE)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
           ->setFormat(oboe::AudioFormat::I16)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setBufferCapacityInFrames(TARGET_FRAMES_PER_CALLBACK * 2);

    const auto result = builder.openStream(temp_stream);
    if (result == oboe::Result::OK) {
        int32_t device_channels = temp_stream->getChannelCount();
        temp_stream->close();
        
        // 基于yuzu的逻辑：如果设备支持6声道则使用6声道，否则使用2声道
        int32_t final_channels = (device_channels >= 6) ? 6 : 2;
        LOGI("Device supports %d channels, using %d channels", device_channels, final_channels);
        return final_channels;
    }

    LOGW("Failed to query device channel count, using default 2 channels");
    return 2;
}

// =============== SimpleRingBuffer Implementation ===============
OboeAudioRenderer::SimpleRingBuffer::SimpleRingBuffer(size_t capacity, int32_t channels) 
    : m_capacity(capacity), m_channels(channels), m_buffer(capacity * channels) {
    LOGI("SimpleRingBuffer created with capacity: %zu frames, %d channels", capacity, channels);
}

OboeAudioRenderer::SimpleRingBuffer::~SimpleRingBuffer() {
    Clear();
}

bool OboeAudioRenderer::SimpleRingBuffer::Write(const int16_t* data, size_t frames, int32_t channels) {
    if (!data || frames == 0 || channels <= 0) return false;
    
    // 如果写入的声道数与缓冲区声道数不匹配，需要转换
    if (channels != m_channels) {
        LOGW("Channel mismatch: write %d channels to %d channel buffer", channels, m_channels);
        return false;
    }
    
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
    
    // 保留一帧的空间避免完全填满
    if (available_space <= samples_needed + channels) {
        LOGW("RingBuffer overflow: needed %zu samples (%d frames, %d channels), available %zu", 
             samples_needed, frames, channels, available_space);
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
    if (!output || frames == 0 || channels <= 0) return 0;
    
    // 确保读取的声道数与缓冲区声道数匹配
    if (channels != m_channels) {
        LOGW("Channel mismatch: read %d channels from %d channel buffer", channels, m_channels);
        return 0;
    }
    
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
    
    // 确保读取的样本数是声道数的整数倍
    samples_to_read = (samples_to_read / channels) * channels;
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

size_t OboeAudioRenderer::SimpleRingBuffer::Available(int32_t channels) const {
    if (channels <= 0) return 0;
    
    size_t write_pos = m_write_pos.load();
    size_t read_pos = m_read_pos.load();
    
    size_t available_samples;
    if (write_pos >= read_pos) {
        available_samples = write_pos - read_pos;
    } else {
        available_samples = m_buffer.size() - read_pos + write_pos;
    }
    
    return available_samples / channels;
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
        // 如果已经初始化但参数不同，需要重新初始化
        if (m_sample_rate.load() != sampleRate || m_app_channel_count.load() != channelCount) {
            LOGI("Audio parameters changed, reinitializing: %dHz %dch -> %dHz %dch", 
                 m_sample_rate.load(), m_app_channel_count.load(), sampleRate, channelCount);
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_app_channel_count.store(channelCount);
    
    // 基于yuzu的逻辑：查询设备支持的声道数
    m_channel_count.store(QueryDeviceChannelCount(oboe::Direction::Output));
    
    LOGI("Initializing Oboe: app requested %dHz %dch, using %dHz %dch", 
         sampleRate, channelCount, sampleRate, m_channel_count.load());
    
    // 计算缓冲区大小 - 根据设备声道数调整
    size_t buffer_capacity = (sampleRate * BUFFER_DURATION_MS) / 1000;
    m_ring_buffer = std::make_unique<SimpleRingBuffer>(buffer_capacity, m_channel_count.load());
    
    if (!OpenStream()) {
        LOGE("Failed to open audio stream for %dHz %dch", sampleRate, m_channel_count.load());
        return false;
    }
    
    m_initialized.store(true);
    LOGI("OboeAudioRenderer successfully initialized");
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

oboe::AudioStreamBuilder* OboeAudioRenderer::ConfigureBuilder(oboe::AudioStreamBuilder& builder, oboe::Direction direction) {
    // 基于yuzu的配置逻辑
    return builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::OpenSLES) // 优先使用OpenSLES，更稳定
           ->setDirection(direction)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
           ->setFormat(oboe::AudioFormat::I16)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setBufferCapacityInFrames(TARGET_FRAMES_PER_CALLBACK * 2)
           ->setChannelCount(m_channel_count.load())
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::OpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置构建器
    ConfigureBuilder(builder, oboe::Direction::Output)
           ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK)
           ->setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 首先尝试OpenSLES（基于yuzu的选择）
    builder.setAudioApi(oboe::AudioApi::OpenSLES);
    auto result = builder.openStream(m_stream);
    
    // 如果OpenSLES失败，尝试AAudio
    if (result != oboe::Result::OK) {
        LOGW("OpenSLES failed, trying AAudio");
        builder.setAudioApi(oboe::AudioApi::AAudio);
        result = builder.openStream(m_stream);
    }
    
    if (result != oboe::Result::OK) {
        LOGE("Failed to open audio stream with both APIs: %d", result);
        return false;
    }
    
    // 设置缓冲区大小
    m_stream->setBufferSizeInFrames(TARGET_FRAMES_PER_CALLBACK * 2);
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start audio stream: %d", result);
        m_stream->close();
        m_stream.reset();
        return false;
    }
    
    m_stream_started.store(true);
    
    LOGI("Audio stream opened: %s, %d channels, %d Hz, buffer size: %d frames", 
         m_stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
         m_stream->getChannelCount(), m_stream->getSampleRate(),
         m_stream->getBufferSizeInFrames());
    
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
    
    int32_t app_channels = m_app_channel_count.load();
    int32_t device_channels = m_channel_count.load();
    
    bool success;
    
    // 处理声道数不匹配的情况
    if (app_channels == device_channels) {
        // 声道数匹配，直接写入
        success = m_ring_buffer->Write(data, num_frames, device_channels);
    } else {
        // 声道数不匹配，需要转换
        LOGW("Channel conversion required: %dch -> %dch", app_channels, device_channels);
        
        if (app_channels == 6 && device_channels == 2) {
            // 6声道转2声道下混
            std::vector<int16_t> converted_data(num_frames * 2);
            for (int32_t frame = 0; frame < num_frames; frame++) {
                const int16_t* in_frame = data + (frame * 6);
                int16_t* out_frame = converted_data.data() + (frame * 2);
                
                // 简单的下混算法 (基于常见的5.1转立体声)
                float left = in_frame[0] + 0.707f * in_frame[2] + 0.707f * in_frame[4] + 0.5f * in_frame[5];
                float right = in_frame[1] + 0.707f * in_frame[2] + 0.707f * in_frame[3] + 0.5f * in_frame[5];
                
                // 限制在int16范围内
                left = std::max(-32768.0f, std::min(32767.0f, left));
                right = std::max(-32768.0f, std::min(32767.0f, right));
                
                out_frame[0] = static_cast<int16_t>(left);
                out_frame[1] = static_cast<int16_t>(right);
            }
            success = m_ring_buffer->Write(converted_data.data(), num_frames, 2);
        } else if (app_channels == 2 && device_channels == 6) {
            // 2声道转6声道上混
            std::vector<int16_t> converted_data(num_frames * 6);
            for (int32_t frame = 0; frame < num_frames; frame++) {
                const int16_t* in_frame = data + (frame * 2);
                int16_t* out_frame = converted_data.data() + (frame * 6);
                
                // 简单的上混算法
                out_frame[0] = in_frame[0]; // 左前
                out_frame[1] = in_frame[1]; // 右前
                out_frame[2] = static_cast<int16_t>((in_frame[0] + in_frame[1]) * 0.5f); // 中置
                out_frame[3] = in_frame[1]; // 右后
                out_frame[4] = in_frame[0]; // 左后
                out_frame[5] = static_cast<int16_t>((in_frame[0] + in_frame[1]) * 0.3f); // 低音
            }
            success = m_ring_buffer->Write(converted_data.data(), num_frames, 6);
        } else {
            LOGE("Unsupported channel conversion: %d -> %d", app_channels, device_channels);
            return false;
        }
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        LOGW("Failed to write %d frames (app: %dch, device: %dch) to ring buffer", 
             num_frames, app_channels, device_channels);
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    return m_ring_buffer ? static_cast<int32_t>(m_ring_buffer->Available(m_channel_count.load())) : 0;
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
        int32_t channels = m_channel_count.load();
        std::memset(audioData, 0, num_frames * channels * sizeof(int16_t));
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
            LOGW("Audio underrun: no data available for %d frames (%d channels)", 
                 num_frames, channels);
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