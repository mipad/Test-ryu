// oboe_audio_renderer.cpp (双音频流共享模式)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

namespace RyujinxOboe {

// =============== RingBuffer Implementation ===============
OboeSinkStream::RingBuffer::RingBuffer(size_t capacity)
    : m_capacity(capacity), m_buffer(capacity), m_read_index(0), m_write_index(0) {}

bool OboeSinkStream::RingBuffer::Write(const float* data, size_t count) {
    if (count == 0) return true;

    std::lock_guard<std::mutex> lock(m_mutex);
    size_t write_index = m_write_index.load();
    size_t read_index = m_read_index.load();

    size_t available = (read_index > write_index) ? (read_index - write_index - 1) : 
                     (m_capacity - write_index + read_index - 1);
    
    if (count > available) {
        // 缓冲区不足，丢弃最旧的数据
        size_t overflow = count - available;
        m_read_index.store((read_index + overflow) % m_capacity);
        LOGI("RingBuffer overflow, dropped %zu frames", overflow / 2);
    }

    size_t end = write_index + count;
    if (end <= m_capacity) {
        std::memcpy(&m_buffer[write_index], data, count * sizeof(float));
    } else {
        size_t part1 = m_capacity - write_index;
        std::memcpy(&m_buffer[write_index], data, part1 * sizeof(float));
        std::memcpy(&m_buffer[0], data + part1, (count - part1) * sizeof(float));
    }

    m_write_index.store((write_index + count) % m_capacity);
    return true;
}

size_t OboeSinkStream::RingBuffer::Read(float* output, size_t count) {
    if (count == 0) return 0;

    std::lock_guard<std::mutex> lock(m_mutex);
    size_t write_index = m_write_index.load();
    size_t read_index = m_read_index.load();

    size_t available_samples = (write_index >= read_index) ? (write_index - read_index) : 
                             (m_capacity - read_index + write_index);
    
    size_t to_read = std::min(count, available_samples);
    if (to_read == 0) {
        return 0;
    }

    size_t end = read_index + to_read;
    if (end <= m_capacity) {
        std::memcpy(output, &m_buffer[read_index], to_read * sizeof(float));
    } else {
        size_t part1 = m_capacity - read_index;
        std::memcpy(output, &m_buffer[read_index], part1 * sizeof(float));
        std::memcpy(output + part1, &m_buffer[0], (to_read - part1) * sizeof(float));
    }

    m_read_index.store((read_index + to_read) % m_capacity);
    return to_read;
}

size_t OboeSinkStream::RingBuffer::Available() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    size_t write = m_write_index.load();
    size_t read = m_read_index.load();
    
    if (write >= read) {
        return write - read;
    } else {
        return m_capacity - read + write;
    }
}

size_t OboeSinkStream::RingBuffer::AvailableForWrite() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    size_t write = m_write_index.load();
    size_t read = m_read_index.load();
    
    if (write >= read) {
        return m_capacity - write + read - 1;
    } else {
        return read - write - 1;
    }
}

void OboeSinkStream::RingBuffer::Clear() {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_read_index.store(0);
    m_write_index.store(0);
}

// =============== OboeSinkStream Implementation ===============
OboeSinkStream::OboeSinkStream(uint32_t system_channels, const char* name, uint32_t sample_rate, int stream_id)
    : m_system_channels(system_channels), m_name(name ? name : "RyujinxAudio"),
      m_sample_rate(sample_rate), m_device_channels(2), m_stream_id(stream_id) {
    
    // 初始化环形缓冲区：200ms 缓冲
    size_t buffer_capacity = (m_sample_rate * m_device_channels * 200) / 1000;
    buffer_capacity = (buffer_capacity + 511) & ~511;
    m_ring_buffer = std::make_unique<RingBuffer>(buffer_capacity);
    
    LOGI("OboeSinkStream created: %s (ID:%d), sample_rate=%u, buffer_capacity=%zu", 
         m_name.c_str(), m_stream_id, m_sample_rate, buffer_capacity);
}

OboeSinkStream::~OboeSinkStream() {
    Finalize();
}

int32_t OboeSinkStream::QueryChannelCount(oboe::Direction direction) {
    std::shared_ptr<oboe::AudioStream> temp_stream;
    oboe::AudioStreamBuilder builder;

    // 使用共享模式，兼容 Android 11+
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Shared)  // 关键修改：使用共享模式
           ->setAudioApi(oboe::AudioApi::Unspecified)   // 让 Oboe 自动选择最佳 API
           ->setDirection(direction)
           ->setSampleRate(48000)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(oboe::AudioFormat::I16)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setBufferCapacityInFrames(TARGET_SAMPLE_COUNT * 4);

    const auto result = builder.openStream(temp_stream);
    if (result == oboe::Result::OK) {
        int32_t channels = temp_stream->getChannelCount();
        LOGI("Detected audio channels: %d", channels);
        return channels >= 6 ? 6 : 2;
    }

    LOGE("Failed to open stream for channel count query. Using default: 2");
    return 2;
}

bool OboeSinkStream::ConfigureStream(oboe::AudioStreamBuilder& builder, oboe::Direction direction) {
    const auto expected_channels = QueryChannelCount(direction);
    const auto expected_mask = [&]() {
        switch (expected_channels) {
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

    // 关键修改：使用共享模式，允许其他应用同时访问音频设备
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Shared)  // 共享模式
           ->setAudioApi(oboe::AudioApi::Unspecified)   // 自动选择最佳 API
           ->setDirection(direction)
           ->setSampleRate(m_sample_rate)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(oboe::AudioFormat::I16)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setChannelCount(expected_channels)
           ->setChannelMask(expected_mask)
           ->setChannelConversionAllowed(true)
           ->setBufferCapacityInFrames(TARGET_SAMPLE_COUNT * 4)
           ->setFramesPerCallback(TARGET_SAMPLE_COUNT)
           ->setDataCallback(this)
           ->setErrorCallback(this);

    return true;
}

bool OboeSinkStream::OpenStream() {
    oboe::AudioStreamBuilder builder;
    if (!ConfigureStream(builder, oboe::Direction::Output)) {
        return false;
    }

    const auto result = builder.openStream(m_stream);
    if (result != oboe::Result::OK) {
        LOGE("Failed to open Oboe stream (ID:%d): %d", m_stream_id, result);
        return false;
    }

    // 设置流属性
    m_device_channels = m_stream->getChannelCount();
    
    // 在共享模式下，让系统管理缓冲区大小
    auto desiredBufferSize = m_stream->getFramesPerBurst() * 4; // 减少缓冲区大小
    m_stream->setBufferSizeInFrames(desiredBufferSize);
    
    int32_t actualBufferSize = m_stream->getBufferSizeInFrames();
    const auto buffer_capacity = m_stream->getBufferCapacityInFrames();
    const auto stream_backend = m_stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES";

    LOGI("Oboe stream opened (ID:%d): %s, %d channels, %d Hz, buffer %d/%d, burst %d, sharing=%s", 
         m_stream_id, stream_backend, m_device_channels, m_sample_rate, 
         actualBufferSize, buffer_capacity, m_stream->getFramesPerBurst(),
         m_stream->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared");

    return true;
}

bool OboeSinkStream::Initialize() {
    if (m_initialized.load()) {
        return true;
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (!OpenStream()) {
        LOGE("Failed to open Oboe stream (ID:%d)", m_stream_id);
        return false;
    }

    m_initialized.store(true);
    m_paused.store(true);
    LOGI("OboeSinkStream initialized successfully (ID:%d)", m_stream_id);
    return true;
}

void OboeSinkStream::Finalize() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_stream) {
        if (m_stream_started.load()) {
            m_stream->stop();
            LOGI("Oboe stream stopped (ID:%d)", m_stream_id);
        }
        m_stream->close();
        m_stream.reset();
    }
    
    if (m_ring_buffer) {
        m_ring_buffer->Clear();
    }
    
    m_stream_started.store(false);
    m_initialized.store(false);
    m_paused.store(true);
    LOGI("OboeSinkStream finalized (ID:%d)", m_stream_id);
}

void OboeSinkStream::Start() {
    if (!m_initialized.load() || !m_stream) {
        return;
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_paused.load()) {
        m_paused.store(false);
        
        auto result = m_stream->start();
        if (result == oboe::Result::OK) {
            m_stream_started.store(true);
            LOGI("Oboe stream started (ID:%d)", m_stream_id);
        } else {
            LOGE("Failed to start Oboe stream (ID:%d): %d", m_stream_id, result);
        }
    }
}

void OboeSinkStream::Stop() {
    if (!m_initialized.load() || !m_stream) {
        return;
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (!m_paused.load()) {
        m_paused.store(true);
        
        if (m_stream_started.load()) {
            auto result = m_stream->stop();
            if (result != oboe::Result::OK) {
                LOGE("Failed to stop Oboe stream (ID:%d): %d", m_stream_id, result);
            }
            m_stream_started.store(false);
            LOGI("Oboe stream stopped (ID:%d)", m_stream_id);
        }
    }
}

bool OboeSinkStream::WriteAudio(const float* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }

    // 确保流已启动
    if (m_paused.load()) {
        Start();
    }

    size_t total_samples = num_frames * m_device_channels;
    
    // 检查缓冲区空间
    if (m_ring_buffer->AvailableForWrite() < total_samples) {
        LOGE("Buffer overflow in stream %d: available=%zu, needed=%zu", 
             m_stream_id, m_ring_buffer->AvailableForWrite(), total_samples);
        return false;
    }
    
    bool success = m_ring_buffer->Write(data, total_samples);
    if (success) {
        m_total_frames_written += num_frames;
    }
    
    return success;
}

int32_t OboeSinkStream::GetBufferedFrames() const {
    if (!m_ring_buffer) {
        return 0;
    }
    return static_cast<int32_t>(m_ring_buffer->Available() / m_device_channels);
}

void OboeSinkStream::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

oboe::DataCallbackResult OboeSinkStream::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    if (!m_initialized.load() || m_paused.load()) {
        // 流暂停或未初始化，输出静音
        std::memset(audioData, 0, num_frames * m_device_channels * sizeof(int16_t));
        return oboe::DataCallbackResult::Continue;
    }

    int16_t* output = static_cast<int16_t*>(audioData);
    size_t total_samples = num_frames * m_device_channels;
    
    std::vector<float> float_data(total_samples);
    size_t read_samples = m_ring_buffer->Read(float_data.data(), total_samples);
    
    // 填充剩余部分为静音
    if (read_samples < total_samples) {
        std::fill(float_data.begin() + read_samples, float_data.end(), 0.0f);
        LOGI("Audio underrun in stream %d: read %zu of %zu samples", m_stream_id, read_samples, total_samples);
    }
    
    // 转换为int16并应用音量
    float volume = m_volume.load();
    for (size_t i = 0; i < total_samples; i++) {
        float sample = float_data[i] * volume;
        sample = std::clamp(sample, -1.0f, 1.0f);
        output[i] = static_cast<int16_t>(sample * 32767.0f);
    }

    m_total_frames_played += num_frames;
    return oboe::DataCallbackResult::Continue;
}

void OboeSinkStream::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGI("Oboe stream closed (ID:%d), error: %d", m_stream_id, error);
    
    // 尝试重新初始化流
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false);
    m_initialized.store(false);
    
    if (OpenStream()) {
        if (!m_paused.load()) {
            m_stream->start();
            m_stream_started.store(true);
        }
        m_initialized.store(true);
        LOGI("Oboe stream recovered after error (ID:%d)", m_stream_id);
    }
}

void OboeSinkStream::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Oboe stream error before close (ID:%d): %d", m_stream_id, error);
    m_stream_started.store(false);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    // 预先创建一个流
    m_sink_streams.push_back(std::make_unique<OboeSinkStream>(2, "RyujinxMainAudio", m_current_sample_rate, 0));
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::GetInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::Initialize() {
    if (m_initialized.load()) {
        return true;
    }

    std::lock_guard<std::mutex> lock(m_init_mutex);
    if (m_initialized.load()) {
        return true;
    }

    // 初始化所有流
    bool all_initialized = true;
    for (auto& stream : m_sink_streams) {
        if (!stream->Initialize()) {
            LOGE("Failed to initialize Oboe sink stream (ID:%d)", stream->GetStreamId());
            all_initialized = false;
        }
    }

    if (!all_initialized && m_sink_streams.empty()) {
        LOGE("No Oboe sink streams could be initialized");
        return false;
    }

    m_initialized.store(true);
    LOGI("Oboe audio renderer initialized successfully with %zu streams", m_sink_streams.size());
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_init_mutex);
    
    for (auto& stream : m_sink_streams) {
        if (stream) {
            stream->Finalize();
        }
    }
    m_sink_streams.clear();
    
    m_initialized.store(false);
    m_stream_count = 0;
    m_current_stream_id = 0;
    LOGI("Oboe audio renderer shutdown");
}

void OboeAudioRenderer::SetSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        return;
    }
    
    m_current_sample_rate = sampleRate;
    
    // 需要重新初始化所有流
    if (m_initialized.load()) {
        std::lock_guard<std::mutex> lock(m_init_mutex);
        for (auto& stream : m_sink_streams) {
            if (stream) {
                stream->Finalize();
                // 重新创建流
                stream = std::make_unique<OboeSinkStream>(2, "RyujinxMainAudio", m_current_sample_rate, stream->GetStreamId());
                stream->Initialize();
            }
        }
    }
}

void OboeAudioRenderer::SetBufferSize(int32_t bufferSize) {
    // 缓冲大小由Oboe自动管理
}

void OboeAudioRenderer::SetVolume(float volume) {
    for (auto& stream : m_sink_streams) {
        if (stream) {
            stream->SetVolume(volume);
        }
    }
}

bool OboeAudioRenderer::WriteAudio(const float* data, int32_t numFrames) {
    return WriteAudioToStream(data, numFrames, m_current_stream_id);
}

bool OboeAudioRenderer::WriteAudioToStream(const float* data, int32_t numFrames, int stream_id) {
    if (!m_initialized.load() && !Initialize()) {
        return false;
    }

    if (!data || numFrames <= 0) {
        return false;
    }

    // 找到对应的流
    for (auto& stream : m_sink_streams) {
        if (stream && stream->GetStreamId() == stream_id) {
            return stream->WriteAudio(data, numFrames);
        }
    }

    LOGE("Stream ID %d not found", stream_id);
    return false;
}

void OboeAudioRenderer::ClearBuffer() {
    if (m_initialized.load()) {
        std::lock_guard<std::mutex> lock(m_init_mutex);
        for (auto& stream : m_sink_streams) {
            if (stream) {
                stream->Finalize();
                stream->Initialize();
            }
        }
    }
}

bool OboeAudioRenderer::IsInitialized() const {
    return m_initialized.load() && !m_sink_streams.empty();
}

bool OboeAudioRenderer::IsPlaying() const {
    for (const auto& stream : m_sink_streams) {
        if (stream && stream->IsPlaying()) {
            return true;
        }
    }
    return false;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    int32_t total_buffered = 0;
    for (const auto& stream : m_sink_streams) {
        if (stream) {
            total_buffered += stream->GetBufferedFrames();
        }
    }
    return total_buffered;
}

uint32_t OboeAudioRenderer::GetSampleRate() const {
    if (!m_sink_streams.empty() && m_sink_streams[0]) {
        return m_sink_streams[0]->GetSampleRate();
    }
    return 48000;
}

uint32_t OboeAudioRenderer::GetChannelCount() const {
    if (!m_sink_streams.empty() && m_sink_streams[0]) {
        return m_sink_streams[0]->GetChannelCount();
    }
    return 2;
}

int64_t OboeAudioRenderer::GetTotalFramesWritten() const {
    int64_t total = 0;
    for (const auto& stream : m_sink_streams) {
        if (stream) {
            total += 0; // 需要在实际实现中添加计数
        }
    }
    return total;
}

int64_t OboeAudioRenderer::GetTotalFramesPlayed() const {
    int64_t total = 0;
    for (const auto& stream : m_sink_streams) {
        if (stream) {
            total += 0; // 需要在实际实现中添加计数
        }
    }
    return total;
}

bool OboeAudioRenderer::CreateAdditionalStream() {
    std::lock_guard<std::mutex> lock(m_init_mutex);
    
    int new_stream_id = m_stream_count;
    auto new_stream = std::make_unique<OboeSinkStream>(2, "RyujinxSecondaryAudio", m_current_sample_rate, new_stream_id);
    
    if (new_stream->Initialize()) {
        m_sink_streams.push_back(std::move(new_stream));
        m_stream_count++;
        LOGI("Created additional audio stream (ID:%d), total streams: %d", new_stream_id, m_stream_count);
        return true;
    }
    
    LOGE("Failed to create additional audio stream");
    return false;
}

bool OboeAudioRenderer::SwitchToStream(int stream_id) {
    // 检查流是否存在
    for (const auto& stream : m_sink_streams) {
        if (stream && stream->GetStreamId() == stream_id) {
            m_current_stream_id = stream_id;
            LOGI("Switched to audio stream ID: %d", stream_id);
            return true;
        }
    }
    
    LOGE("Cannot switch to non-existent stream ID: %d", stream_id);
    return false;
}

} // namespace RyujinxOboe