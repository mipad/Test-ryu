// oboe_audio_renderer.cpp (添加ADPCM支持)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>
#include <array>

namespace RyujinxOboe {

// ADPCM 步长表
static const int16_t ADPCM_STEP_TABLE[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
    253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
    1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
    3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
    12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
};

// ADPCM 索引调整表
static const int8_t ADPCM_INDEX_TABLE[16] = {
    -1, -1, -1, -1, 2, 4, 6, 8,
    -1, -1, -1, -1, 2, 4, 6, 8
};

// =============== ADPCM 解码实现 ===============
void OboeAudioRenderer::ResetAdpcmState(AdpcmState& state) {
    state.predictor = 0;
    state.step_index = 0;
    state.step = ADPCM_STEP_TABLE[state.step_index];
    state.history[0] = 0;
    state.history[1] = 0;
}

int16_t OboeAudioRenderer::DecodeAdpcmSample(uint8_t nibble, AdpcmState& state) {
    // 获取符号和幅度
    int8_t sign = nibble & 8;
    int8_t delta = nibble & 7;
    
    // 计算差值
    int32_t diff = state.step >> 3;
    if (delta & 4) diff += state.step;
    if (delta & 2) diff += state.step >> 1;
    if (delta & 1) diff += state.step >> 2;
    
    // 应用符号
    if (sign) {
        state.predictor -= diff;
    } else {
        state.predictor += diff;
    }
    
    // 钳制预测器值
    if (state.predictor > 32767) {
        state.predictor = 32767;
    } else if (state.predictor < -32768) {
        state.predictor = -32768;
    }
    
    // 更新步长索引
    state.step_index += ADPCM_INDEX_TABLE[delta];
    if (state.step_index < 0) {
        state.step_index = 0;
    } else if (state.step_index > 88) {
        state.step_index = 88;
    }
    
    // 更新步长
    state.step = ADPCM_STEP_TABLE[state.step_index];
    
    return static_cast<int16_t>(state.predictor);
}

bool OboeAudioRenderer::DecodeAdpcm(const uint8_t* adpcm_data, size_t adpcm_size, std::vector<int16_t>& pcm_output, AdpcmState& state) {
    if (!adpcm_data || adpcm_size == 0) {
        return false;
    }
    
    // 每个ADPCM字节包含2个样本
    size_t num_samples = adpcm_size * 2;
    pcm_output.resize(num_samples);
    
    for (size_t i = 0; i < adpcm_size; ++i) {
        uint8_t byte = adpcm_data[i];
        
        // 解码高4位
        uint8_t high_nibble = (byte >> 4) & 0x0F;
        pcm_output[i * 2] = DecodeAdpcmSample(high_nibble, state);
        
        // 解码低4位
        uint8_t low_nibble = byte & 0x0F;
        pcm_output[i * 2 + 1] = DecodeAdpcmSample(low_nibble, state);
    }
    
    return true;
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
    ResetAdpcmState(m_adpcm_state);
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
    
    // 重置ADPCM状态
    ResetAdpcmState(m_adpcm_state);
    
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
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
        m_raw_sample_queue.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    // AAudio 独占模式配置
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game);
    
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
    if (!OptimizeBufferSize()) {
        CloseStream();
        return false;
    }
    
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

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) {
        return false;
    }
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    int32_t desired_buffer_size;
    
    if (framesPerBurst > 0) {
        // 使用 FramesPerBurst * 2 作为缓冲区大小（标准低延迟配置）
        desired_buffer_size = framesPerBurst * 2;
    } else {
        // 无法获取 FramesPerBurst，使用固定值
        desired_buffer_size = 960; // 240 * 4
    }
    
    auto setBufferResult = m_stream->setBufferSizeInFrames(desired_buffer_size);
    
    // 记录实际的缓冲区大小
    int32_t actual_buffer_size = m_stream->getBufferSizeInFrames();
    
    m_frames_per_burst.store(framesPerBurst);
    m_buffer_size.store(actual_buffer_size);
    
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
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    bool success = false;
    
    // 处理不同的格式
    switch (sampleFormat) {
        case ADPCM: {
            // ADPCM 解码为 PCM16
            int32_t system_channels = m_channel_count.load();
            size_t adpcm_size = num_frames * system_channels / 2; // ADPCM 是 4:1 压缩
            
            std::vector<int16_t> pcm_data;
            if (DecodeAdpcm(byte_data, adpcm_size, pcm_data, m_adpcm_state)) {
                size_t pcm_size = pcm_data.size() * sizeof(int16_t);
                success = m_raw_sample_queue->WriteRaw(
                    reinterpret_cast<const uint8_t*>(pcm_data.data()), 
                    pcm_size, 
                    PCM_INT16);
            }
            break;
        }
        
        case PCM_INT8: {
            // PCM8 转换为 PCM16
            int32_t system_channels = m_channel_count.load();
            size_t pcm8_size = num_frames * system_channels;
            std::vector<int16_t> pcm16_data(pcm8_size);
            
            const int8_t* pcm8_data = reinterpret_cast<const int8_t*>(data);
            for (size_t i = 0; i < pcm8_size; ++i) {
                pcm16_data[i] = static_cast<int16_t>(pcm8_data[i]) * 256; // 左移8位
            }
            
            size_t pcm16_size = pcm16_data.size() * sizeof(int16_t);
            success = m_raw_sample_queue->WriteRaw(
                reinterpret_cast<const uint8_t*>(pcm16_data.data()), 
                pcm16_size, 
                PCM_INT16);
            break;
        }
        
        default: {
            // 直接支持格式：PCM16, PCM24, PCM32, Float
            int32_t system_channels = m_channel_count.load();
            size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
            size_t data_size = num_frames * system_channels * bytes_per_sample;
            
            success = m_raw_sample_queue->WriteRaw(byte_data, data_size, sampleFormat);
            break;
        }
    }
    
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
    
    // 重置ADPCM状态
    ResetAdpcmState(m_adpcm_state);
    
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT8:   return oboe::AudioFormat::I16;  // PCM8 转换为 PCM16
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        case ADPCM:      return oboe::AudioFormat::I16;  // ADPCM 解码为 PCM16
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
        case ADPCM:      return "ADPCM";
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
        case ADPCM:      return 0; // ADPCM 是压缩格式，不直接计算字节数
        default:         return 2; // 默认PCM16
    }
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
        m_underrun_count++;
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
    stats.sample_rate = m_sample_rate.load();
    stats.frames_per_burst = m_frames_per_burst.load();
    stats.buffer_size = m_buffer_size.load();
    return stats;
}

} // namespace RyujinxOboe