// oboe_audio_renderer.cpp (声道修复版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

// =============== ChannelAwareRingBuffer Implementation ===============
OboeAudioRenderer::ChannelAwareRingBuffer::ChannelAwareRingBuffer(size_t capacity, int32_t channels) 
    : m_samples_capacity(capacity * channels),
      m_buffer(m_samples_capacity),
      m_capacity(capacity),
      m_channels(channels) {
    LOGI("ChannelAwareRingBuffer: %zu frames, %d channels, %zu samples", 
         capacity, channels, m_samples_capacity);
}

OboeAudioRenderer::ChannelAwareRingBuffer::~ChannelAwareRingBuffer() {
    Clear();
}

bool OboeAudioRenderer::ChannelAwareRingBuffer::Write(const int16_t* data, size_t frames, int32_t input_channels) {
    if (!data || frames == 0) return false;
    
    // 如果输入声道数与缓冲区声道数不匹配，进行转换
    if (input_channels != m_channels) {
        return ConvertAndWrite(data, frames, input_channels);
    }
    
    // 直接写入，声道数匹配
    size_t samples_needed = frames * m_channels;
    
    // 检查是否有足够空间
    if (GetFreeSpace() < samples_needed) {
        return false;
    }
    
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    
    // 写入数据
    size_t end_pos = write_pos + samples_needed;
    if (end_pos <= m_samples_capacity) {
        std::memcpy(&m_buffer[write_pos], data, samples_needed * sizeof(int16_t));
    } else {
        size_t first_part = m_samples_capacity - write_pos;
        std::memcpy(&m_buffer[write_pos], data, first_part * sizeof(int16_t));
        std::memcpy(&m_buffer[0], data + first_part, (samples_needed - first_part) * sizeof(int16_t));
    }
    
    m_write_pos.store((write_pos + samples_needed) % m_samples_capacity, std::memory_order_release);
    return true;
}

bool OboeAudioRenderer::ChannelAwareRingBuffer::ConvertAndWrite(const int16_t* data, size_t frames, int32_t input_channels) {
    LOGI("Channel conversion required: %d -> %d", input_channels, m_channels);
    
    if (input_channels == 6 && m_channels == 2) {
        // 6声道转2声道下混
        std::vector<int16_t> converted_data;
        return Convert6To2(data, frames, converted_data) && 
               Write(converted_data.data(), frames, 2);
    } 
    else if (input_channels == 2 && m_channels == 6) {
        // 2声道转6声道上混
        std::vector<int16_t> converted_data;
        return Convert2To6(data, frames, converted_data) && 
               Write(converted_data.data(), frames, 6);
    }
    else {
        LOGE("Unsupported channel conversion: %d -> %d", input_channels, m_channels);
        return false;
    }
}

bool OboeAudioRenderer::ChannelAwareRingBuffer::Convert6To2(const int16_t* data, size_t frames, std::vector<int16_t>& output) {
    output.resize(frames * 2);
    
    for (size_t frame = 0; frame < frames; frame++) {
        const int16_t* in_frame = data + (frame * 6);
        int16_t* out_frame = output.data() + (frame * 2);
        
        // 标准的5.1转立体声下混算法
        // 左声道 = 左前 + 0.707*中置 + 0.707*左环绕 + 0.5*低音
        float left = in_frame[0] + 0.707f * in_frame[2] + 0.707f * in_frame[4] + 0.5f * in_frame[5];
        // 右声道 = 右前 + 0.707*中置 + 0.707*右环绕 + 0.5*低音  
        float right = in_frame[1] + 0.707f * in_frame[2] + 0.707f * in_frame[3] + 0.5f * in_frame[5];
        
        // 限制在int16范围内并防止溢出
        left = std::max(-32768.0f, std::min(32767.0f, left));
        right = std::max(-32768.0f, std::min(32767.0f, right));
        
        out_frame[0] = static_cast<int16_t>(left);
        out_frame[1] = static_cast<int16_t>(right);
    }
    
    return true;
}

bool OboeAudioRenderer::ChannelAwareRingBuffer::Convert2To6(const int16_t* data, size_t frames, std::vector<int16_t>& output) {
    output.resize(frames * 6);
    
    for (size_t frame = 0; frame < frames; frame++) {
        const int16_t* in_frame = data + (frame * 2);
        int16_t* out_frame = output.data() + (frame * 6);
        
        // 简单的立体声转5.1上混算法
        out_frame[0] = in_frame[0];                    // 左前
        out_frame[1] = in_frame[1];                    // 右前
        out_frame[2] = static_cast<int16_t>((in_frame[0] + in_frame[1]) * 0.5f);  // 中置
        out_frame[3] = static_cast<int16_t>(in_frame[1] * 0.7f);                  // 右环绕
        out_frame[4] = static_cast<int16_t>(in_frame[0] * 0.7f);                  // 左环绕
        out_frame[5] = static_cast<int16_t>((in_frame[0] + in_frame[1]) * 0.3f);  // 低音
    }
    
    return true;
}

size_t OboeAudioRenderer::ChannelAwareRingBuffer::Read(int16_t* output, size_t frames) {
    if (!output || frames == 0) return 0;
    
    size_t samples_requested = frames * m_channels;
    size_t available = Available() * m_channels;
    
    size_t samples_to_read = std::min(samples_requested, available);
    if (samples_to_read == 0) {
        return 0;
    }
    
    // 确保读取的样本数是声道数的整数倍
    samples_to_read = (samples_to_read / m_channels) * m_channels;
    if (samples_to_read == 0) {
        return 0;
    }
    
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    // 读取数据
    size_t end_pos = read_pos + samples_to_read;
    if (end_pos <= m_samples_capacity) {
        std::memcpy(output, &m_buffer[read_pos], samples_to_read * sizeof(int16_t));
    } else {
        size_t first_part = m_samples_capacity - read_pos;
        std::memcpy(output, &m_buffer[read_pos], first_part * sizeof(int16_t));
        std::memcpy(output + first_part, &m_buffer[0], (samples_to_read - first_part) * sizeof(int16_t));
    }
    
    m_read_pos.store((read_pos + samples_to_read) % m_samples_capacity, std::memory_order_release);
    return samples_to_read / m_channels;
}

size_t OboeAudioRenderer::ChannelAwareRingBuffer::Available() const {
    size_t write_pos = m_write_pos.load(std::memory_order_acquire);
    size_t read_pos = m_read_pos.load(std::memory_order_acquire);
    
    if (write_pos >= read_pos) {
        return (write_pos - read_pos) / m_channels;
    } else {
        return (m_samples_capacity - read_pos + write_pos) / m_channels;
    }
}

size_t OboeAudioRenderer::ChannelAwareRingBuffer::GetFreeSpace() const {
    size_t available = Available();
    return m_capacity - available - 1; // 保留一个样本避免完全填满
}

void OboeAudioRenderer::ChannelAwareRingBuffer::Clear() {
    m_read_pos.store(0, std::memory_order_release);
    m_write_pos.store(0, std::memory_order_release);
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::ChannelAwareAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::ChannelAwareErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Audio stream closed with error: %d", error);
    m_renderer->OnStreamError(error);
}

void OboeAudioRenderer::ChannelAwareErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Audio stream error before close: %d", error);
    m_renderer->OnStreamError(error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<ChannelAwareAudioCallback>(this);
    m_error_callback = std::make_unique<ChannelAwareErrorCallback>(this);
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
        // 检查是否需要重新初始化
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
    
    LOGI("Initializing Oboe: %dHz %dch", sampleRate, channelCount);
    
    // 计算缓冲区大小
    size_t buffer_capacity = (sampleRate * BUFFER_DURATION_MS) / 1000;
    m_ring_buffer = std::make_unique<ChannelAwareRingBuffer>(buffer_capacity, channelCount);
    
    if (!ConfigureAndOpenStream()) {
        LOGE("Failed to open audio stream for %dHz %dch", sampleRate, channelCount);
        return false;
    }
    
    m_initialized.store(true);
    LOGI("OboeAudioRenderer initialized successfully for %d channels", channelCount);
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

void OboeAudioRenderer::ConfigureForChannels(oboe::AudioStreamBuilder& builder) {
    // 根据声道数配置
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Shared)  // 共享模式兼容性更好
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(m_channel_count.load())
           ->setSampleRate(m_sample_rate.load())
           ->setFramesPerCallback(TARGET_FRAMES_PER_CALLBACK)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormatConversionAllowed(true)
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置参数
    ConfigureForChannels(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 优先使用OpenSLES，它在声道处理上更稳定
    bool success = false;
    
    // 尝试1: OpenSLES
    builder.setAudioApi(oboe::AudioApi::OpenSLES);
    auto result = builder.openStream(m_stream);
    
    if (result == oboe::Result::OK) {
        LOGI("Using OpenSLES for %d channels", m_channel_count.load());
        success = true;
    } else {
        LOGW("OpenSLES failed, trying AAudio");
        
        // 尝试2: AAudio
        builder.setAudioApi(oboe::AudioApi::AAudio);
        result = builder.openStream(m_stream);
        
        if (result == oboe::Result::OK) {
            LOGI("Using AAudio for %d channels", m_channel_count.load());
            success = true;
        }
    }
    
    if (!success) {
        LOGE("All audio API attempts failed for %d channels", m_channel_count.load());
        return false;
    }
    
    // 设置合适的缓冲区大小
    int32_t desired_buffer_size = TARGET_FRAMES_PER_CALLBACK * 2;
    auto setBufferResult = m_stream->setBufferSizeInFrames(desired_buffer_size);
    if (setBufferResult) {
        LOGD("Buffer size set to %d frames", setBufferResult.value());
    }
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start audio stream: %d", result);
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    
    // 记录流信息
    LOGI("Audio stream: %s, %d channels, %d Hz, buffer: %d/%d frames",
         m_stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
         m_stream->getChannelCount(),
         m_stream->getSampleRate(),
         m_stream->getBufferSizeInFrames(),
         m_stream->getBufferCapacityInFrames());
    
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

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames, int32_t input_channels) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        m_write_failures++;
        return false;
    }
    
    if (!m_ring_buffer) {
        m_write_failures++;
        return false;
    }
    
    // 检查输入声道数是否合理
    if (input_channels != 2 && input_channels != 6) {
        LOGE("Unsupported input channel count: %d", input_channels);
        m_write_failures++;
        return false;
    }
    
    // 应用音量
    float volume = m_volume.load();
    bool apply_volume = (volume != 1.0f);
    
    bool success;
    
    if (apply_volume) {
        // 需要应用音量，创建临时缓冲区
        std::vector<int16_t> volume_adjusted(num_frames * input_channels);
        for (int32_t i = 0; i < num_frames * input_channels; i++) {
            volume_adjusted[i] = static_cast<int16_t>(data[i] * volume);
        }
        success = m_ring_buffer->Write(volume_adjusted.data(), num_frames, input_channels);
    } else {
        // 直接写入，无音量调整
        success = m_ring_buffer->Write(data, num_frames, input_channels);
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_write_failures++;
        LOGW("Audio write failed: %d frames, %d input channels, %d buffer channels", 
             num_frames, input_channels, m_ring_buffer->GetChannels());
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
    
    LOGI("Resetting audio stream for %d channels", m_channel_count.load());
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
    }
    
    // 重新配置和打开流
    CloseStream();
    ConfigureAndOpenStream();
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
    
    // 从环形缓冲区读取数据
    size_t frames_read = m_ring_buffer->Read(output, num_frames);
    
    // 如果数据不足，填充静音
    if (frames_read < static_cast<size_t>(num_frames)) {
        size_t samples_remaining = (num_frames - frames_read) * channels;
        std::memset(output + (frames_read * channels), 0, samples_remaining * sizeof(int16_t));
        
        static int underflow_log_counter = 0;
        if (++underflow_log_counter >= 10) {
            LOGW("Audio underflow: %zu/%d frames available for %d channels", 
                 frames_read, num_frames, channels);
            underflow_log_counter = 0;
        }
    }
    
    m_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::Result error) {
    LOGW("Stream error: %d for %d channels", error, m_channel_count.load());
    
    // 简单的错误处理，上层逻辑会处理重置
}

} // namespace RyujinxOboe