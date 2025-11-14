// oboe_audio_renderer.cpp (完整优化版本)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>
#include <array>
#include <limits>

namespace RyujinxOboe {

// =============== AudioFormatConverter 实现 ===============

bool AudioFormatConverter::ConvertToPCM16(const uint8_t* input, int16_t* output,
                                         size_t sample_count, SampleFormat format) {
    if (!input || !output) return false;
    
    switch (format) {
        case SampleFormat::PCM8:
            ConvertPCM8ToPCM16(input, output, sample_count);
            break;
        case SampleFormat::PCM16:
            // 直接拷贝
            std::memcpy(output, input, sample_count * sizeof(int16_t));
            break;
        case SampleFormat::PCM24:
            ConvertPCM24ToPCM16(input, output, sample_count);
            break;
        case SampleFormat::PCM32:
            ConvertPCM32ToPCM16(reinterpret_cast<const int32_t*>(input), output, sample_count);
            break;
        case SampleFormat::PCMFloat:
            ConvertFloatToPCM16(reinterpret_cast<const float*>(input), output, sample_count);
            break;
        default:
            return false;
    }
    return true;
}

void AudioFormatConverter::ConvertPCM8ToPCM16(const uint8_t* input, int16_t* output, size_t sample_count) {
    for (size_t i = 0; i < sample_count; ++i) {
        // 将8位有符号转换为16位
        output[i] = static_cast<int16_t>((static_cast<int32_t>(input[i]) - 128) * 256);
    }
}

void AudioFormatConverter::ConvertPCM24ToPCM16(const uint8_t* input, int16_t* output, size_t sample_count) {
    for (size_t i = 0; i < sample_count; ++i) {
        const uint8_t* sample = input + i * 3;
        // 24位有符号转换为16位（取高16位）
        int32_t value = (static_cast<int32_t>(sample[0]) << 8) |
                       (static_cast<int32_t>(sample[1]) << 16) |
                       (static_cast<int32_t>(sample[2]) << 24);
        output[i] = static_cast<int16_t>(value >> 16);
    }
}

void AudioFormatConverter::ConvertPCM32ToPCM16(const int32_t* input, int16_t* output, size_t sample_count) {
    for (size_t i = 0; i < sample_count; ++i) {
        // 32位有符号转换为16位（取高16位）
        output[i] = static_cast<int16_t>(input[i] >> 16);
    }
}

void AudioFormatConverter::ConvertFloatToPCM16(const float* input, int16_t* output, size_t sample_count) {
    for (size_t i = 0; i < sample_count; ++i) {
        float sample = input[i] * 32768.0f;
        sample = std::clamp(sample, -32768.0f, 32767.0f);
        output[i] = static_cast<int16_t>(sample);
    }
}

void AudioFormatConverter::ApplyVolume(int16_t* samples, size_t sample_count, float volume) {
    if (volume == 1.0f) return;
    
    for (size_t i = 0; i < sample_count; ++i) {
        float sample = static_cast<float>(samples[i]) * volume;
        samples[i] = static_cast<int16_t>(std::clamp(sample, 
            static_cast<float>(std::numeric_limits<int16_t>::min()),
            static_cast<float>(std::numeric_limits<int16_t>::max())));
    }
}

// =============== ChannelDownmixer 实现 ===============

void ChannelDownmixer::Downmix51ToStereo(const int16_t* input, int16_t* output,
                                        size_t frame_count, const float* coefficients) {
    static const float default_coeffs[4] = {1.0f, 0.707f, 0.251f, 0.707f};
    const float* coeffs = coefficients ? coefficients : default_coeffs;
    
    for (size_t i = 0; i < frame_count; ++i) {
        const int16_t* frame = input + i * 6;
        
        float front_left = frame[0];
        float front_right = frame[1];
        float center = frame[2];
        float lfe = frame[3];
        float back_left = frame[4];
        float back_right = frame[5];
        
        // 应用下混系数
        float left = front_left * coeffs[0] + 
                    center * coeffs[1] + 
                    back_left * coeffs[3] +
                    lfe * 0.5f;
        
        float right = front_right * coeffs[0] + 
                     center * coeffs[1] + 
                     back_right * coeffs[3] +
                     lfe * 0.5f;
        
        output[i * 2] = static_cast<int16_t>(std::clamp(left,
            static_cast<float>(std::numeric_limits<int16_t>::min()),
            static_cast<float>(std::numeric_limits<int16_t>::max())));
        
        output[i * 2 + 1] = static_cast<int16_t>(std::clamp(right,
            static_cast<float>(std::numeric_limits<int16_t>::min()),
            static_cast<float>(std::numeric_limits<int16_t>::max())));
    }
}

void ChannelDownmixer::DownmixStereoToMono(const int16_t* input, int16_t* output,
                                          size_t frame_count) {
    for (size_t i = 0; i < frame_count; ++i) {
        int32_t left = input[i * 2];
        int32_t right = input[i * 2 + 1];
        output[i] = static_cast<int16_t>((left + right) / 2);
    }
}

void ChannelDownmixer::RemapChannels(const int16_t* input, int16_t* output,
                                    size_t frame_count, int input_channels,
                                    int output_channels, const int* channel_map) {
    // 简单的声道重映射：channel_map 指定了输出声道从哪个输入声道取值
    for (size_t i = 0; i < frame_count; ++i) {
        for (int out_ch = 0; out_ch < output_channels; ++out_ch) {
            int in_ch = channel_map[out_ch];
            if (in_ch >= 0 && in_ch < input_channels) {
                output[i * output_channels + out_ch] = input[i * input_channels + in_ch];
            } else {
                output[i * output_channels + out_ch] = 0;
            }
        }
    }
}

// =============== HighPerformanceRingBuffer 实现 ===============

HighPerformanceRingBuffer::HighPerformanceRingBuffer(size_t capacity) 
    : capacity_(capacity) {
    buffer_.resize(capacity);
}

size_t HighPerformanceRingBuffer::WriteBulk(const int16_t* data, size_t sample_count) {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (sample_count > capacity_ - size_) {
        sample_count = capacity_ - size_;
    }
    
    if (sample_count == 0) return 0;
    
    // 计算连续写入空间
    size_t first_chunk = std::min(sample_count, capacity_ - tail_);
    std::memcpy(buffer_.data() + tail_, data, first_chunk * sizeof(int16_t));
    
    if (first_chunk < sample_count) {
        std::memcpy(buffer_.data(), data + first_chunk, 
                   (sample_count - first_chunk) * sizeof(int16_t));
    }
    
    tail_ = (tail_ + sample_count) % capacity_;
    size_ += sample_count;
    
    return sample_count;
}

size_t HighPerformanceRingBuffer::ReadBulk(int16_t* output, size_t samples_requested) {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (samples_requested > size_) {
        samples_requested = size_;
    }
    
    if (samples_requested == 0) return 0;
    
    // 计算连续读取空间
    size_t first_chunk = std::min(samples_requested, capacity_ - head_);
    std::memcpy(output, buffer_.data() + head_, first_chunk * sizeof(int16_t));
    
    if (first_chunk < samples_requested) {
        std::memcpy(output + first_chunk, buffer_.data(),
                   (samples_requested - first_chunk) * sizeof(int16_t));
    }
    
    head_ = (head_ + samples_requested) % capacity_;
    size_ -= samples_requested;
    
    return samples_requested;
}

size_t HighPerformanceRingBuffer::Available() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return size_;
}

void HighPerformanceRingBuffer::Clear() {
    std::lock_guard<std::mutex> lock(mutex_);
    head_ = 0;
    tail_ = 0;
    size_ = 0;
}

// =============== AudioResampler 实现 ===============

void AudioResampler::ResampleLinear(const int16_t* input, int16_t* output,
                                   size_t input_frames, size_t output_frames,
                                   int channels, double ratio) {
    // 简单的线性重采样
    for (size_t out_idx = 0; out_idx < output_frames; ++out_idx) {
        double in_idx = out_idx * ratio;
        size_t in_idx0 = static_cast<size_t>(in_idx);
        size_t in_idx1 = in_idx0 + 1;
        double frac = in_idx - in_idx0;
        
        for (int ch = 0; ch < channels; ++ch) {
            int16_t sample0 = (in_idx0 < input_frames) ? input[in_idx0 * channels + ch] : 0;
            int16_t sample1 = (in_idx1 < input_frames) ? input[in_idx1 * channels + ch] : 0;
            
            double interpolated = sample0 * (1.0 - frac) + sample1 * frac;
            output[out_idx * channels + ch] = static_cast<int16_t>(interpolated);
        }
    }
}

void AudioResampler::ResampleHighQuality(const int16_t* input, int16_t* output,
                                        size_t input_frames, size_t output_frames,
                                        int channels, double ratio) {
    // 这里可以实现更高质量的重采样，例如使用多相滤波器
    // 由于复杂度较高，这里暂时使用线性重采样
    ResampleLinear(input, output, input_frames, output_frames, channels, ratio);
}

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
            }
        }
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
    
    // 简化的写入逻辑 - 假设C#端已经处理好所有转换
    int32_t channels = m_channel_count.load();
    size_t total_samples = num_frames * channels;
    
    // 直接写入数据，不进行任何处理
    bool success = m_sample_queue->Write(data, total_samples);
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
    }
    
    return success;
}

bool OboeAudioRenderer::WriteAudioConverted(const uint8_t* data, int32_t num_frames, 
                                           SampleFormat format, int32_t input_channels) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    if (!m_sample_queue) {
        return false;
    }
    
    int32_t output_channels = m_channel_count.load();
    size_t input_sample_count = num_frames * input_channels;
    size_t output_sample_count = num_frames * output_channels;
    
    // 步骤1：格式转换
    std::vector<int16_t> converted_data;
    if (format != SampleFormat::PCM16) {
        converted_data.resize(input_sample_count);
        if (!AudioFormatConverter::ConvertToPCM16(data, converted_data.data(), input_sample_count, format)) {
            return false;
        }
    } else {
        // 如果是PCM16，直接使用数据
        converted_data.assign(reinterpret_cast<const int16_t*>(data), 
                             reinterpret_cast<const int16_t*>(data) + input_sample_count);
    }
    
    // 步骤2：声道下混（如果需要）
    std::vector<int16_t> final_data;
    const int16_t* audio_data = converted_data.data();
    size_t final_sample_count = input_sample_count;
    
    if (input_channels != output_channels) {
        final_data.resize(output_sample_count);
        
        if (input_channels == 6 && output_channels == 2) {
            ChannelDownmixer::Downmix51ToStereo(converted_data.data(), final_data.data(), num_frames);
        } else if (input_channels == 2 && output_channels == 1) {
            ChannelDownmixer::DownmixStereoToMono(converted_data.data(), final_data.data(), num_frames);
        } else {
            // 不支持的声道转换
            return false;
        }
        
        audio_data = final_data.data();
        final_sample_count = output_sample_count;
    }
    
    // 步骤3：应用音量
    AudioFormatConverter::ApplyVolume(const_cast<int16_t*>(audio_data), final_sample_count, m_volume.load());
    
    // 步骤4：写入队列
    bool success = m_sample_queue->Write(audio_data, final_sample_count);
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
    }
    
    return success;
}

bool OboeAudioRenderer::WriteAudioWithDownmix(const int16_t* data, int32_t num_frames,
                                             int32_t input_channels, int32_t output_channels) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    if (!m_sample_queue) {
        return false;
    }
    
    size_t input_sample_count = num_frames * input_channels;
    size_t output_sample_count = num_frames * output_channels;
    
    // 声道下混
    std::vector<int16_t> downmixed_data;
    const int16_t* final_data = data;
    size_t final_sample_count = input_sample_count;
    
    if (input_channels != output_channels) {
        downmixed_data.resize(output_sample_count);
        
        if (input_channels == 6 && output_channels == 2) {
            ChannelDownmixer::Downmix51ToStereo(data, downmixed_data.data(), num_frames);
        } else if (input_channels == 2 && output_channels == 1) {
            ChannelDownmixer::DownmixStereoToMono(data, downmixed_data.data(), num_frames);
        } else {
            // 不支持的声道转换
            return false;
        }
        
        final_data = downmixed_data.data();
        final_sample_count = output_sample_count;
    }
    
    // 应用音量
    AudioFormatConverter::ApplyVolume(const_cast<int16_t*>(final_data), final_sample_count, m_volume.load());
    
    // 写入队列
    bool success = m_sample_queue->Write(final_data, final_sample_count);
    
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

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_sample_queue) {
        m_sample_queue->Clear();
    }
    
    CloseStream();
    ConfigureAndOpenStream();
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

} // namespace RyujinxOboe