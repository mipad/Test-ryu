#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

namespace RyujinxOboe {

OboeAudioRenderer::OboeAudioRenderer() 
    : m_last_write_time(std::chrono::steady_clock::now()) {
    m_audio_callback = std::make_unique<SimpleAudioCallback>(this);
    m_error_callback = std::make_unique<SimpleErrorCallback>(this);
    InitializePool(16);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

void OboeAudioRenderer::InitializePool(size_t size) {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    m_block_pool.reserve(size);
    
    for (size_t i = 0; i < size; ++i) {
        auto block = std::make_unique<AudioBlock>();
        block->clear();
        m_block_pool.push_back(std::move(block));
    }
}

std::unique_ptr<AudioBlock> OboeAudioRenderer::AcquireBlock() {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    
    if (!m_block_pool.empty()) {
        auto block = std::move(m_block_pool.back());
        m_block_pool.pop_back();
        return block;
    }
    
    return std::make_unique<AudioBlock>();
}

void OboeAudioRenderer::ReleaseBlock(std::unique_ptr<AudioBlock> block) {
    if (!block) return;
    
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    block->clear();
    m_block_pool.push_back(std::move(block));
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat) {
    if (m_initialized.load()) {
        // 如果参数相同且流正常，直接返回成功
        if (m_sample_rate.load() == sampleRate && 
            m_channel_count.load() == channelCount &&
            m_sample_format.load() == sampleFormat &&
            m_stream_started.load()) {
            return true;
        }
        Shutdown();
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    m_sample_format.store(sampleFormat);
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    m_consecutive_underruns.store(0);
    m_last_write_time = std::chrono::steady_clock::now();
    
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_stream) {
        m_stream->stop();
        m_stream->close();
        m_stream.reset();
    }
    
    ClearAllBuffers();
    
    m_initialized.store(false);
    m_stream_started.store(false);
    m_needs_restart.store(false);
}

void OboeAudioRenderer::ClearAllBuffers() {
    // 释放当前块
    if (m_current_block) {
        ReleaseBlock(std::move(m_current_block));
    }
    
    // 清理队列中的所有块
    std::unique_ptr<AudioBlock> block;
    while (m_audio_queue.pop(block)) {
        ReleaseBlock(std::move(block));
    }
    
    m_audio_queue.clear();
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    // 配置基本参数
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setFormat(MapSampleFormat(m_sample_format.load()))
           ->setChannelCount(m_channel_count.load())
           ->setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 启用所有可能的转换
    builder.setFormatConversionAllowed(true)
           ->setChannelConversionAllowed(true)
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium);
    
    // 设置缓冲区大小
    int32_t desiredFramesPerCallback = 256;
    builder.setFramesPerCallback(desiredFramesPerCallback);
    
    // 尝试打开流
    oboe::Result result = builder.openStream(m_stream);
    if (result != oboe::Result::OK) {
        // 如果AAudio失败，尝试OpenSL ES
        builder.setAudioApi(oboe::AudioApi::OpenSLES);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            return false;
        }
    }
    
    // 优化缓冲区大小
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    if (framesPerBurst > 0) {
        // 设置缓冲区大小为2个突发
        int32_t bufferSize = framesPerBurst * 2;
        m_stream->setBufferSizeInFrames(bufferSize);
    }
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        m_stream->close();
        m_stream.reset();
        return false;
    }
    
    m_stream_started.store(true);
    m_needs_restart.store(false);
    
    return true;
}

bool OboeAudioRenderer::TryRestartStream() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (!m_initialized.load()) {
        return false;
    }
    
    // 关闭当前流
    if (m_stream) {
        m_stream->stop();
        m_stream->close();
        m_stream.reset();
    }
    
    ClearAllBuffers();
    
    // 等待一小段时间
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    
    // 重新配置和打开
    bool success = ConfigureAndOpenStream();
    
    if (success) {
        m_needs_restart.store(false);
        m_consecutive_underruns.store(0);
    }
    
    return success;
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat) {
    if (!m_initialized.load() || !data || num_frames <= 0) return false;
    
    // 检查是否需要重启流
    if (m_needs_restart.load()) {
        if (!TryRestartStream()) {
            return false;
        }
    }
    
    // 计算总字节数
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t bytes_per_frame = bytes_per_sample * m_channel_count.load();
    size_t total_bytes = num_frames * bytes_per_frame;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    m_last_write_time = std::chrono::steady_clock::now();
    
    while (bytes_remaining > 0) {
        auto block = AcquireBlock();
        if (!block) {
            // 对象池耗尽，标记需要重启
            m_needs_restart.store(true);
            return false;
        }
        
        size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
        
        // 简单复制数据，让Oboe处理格式转换
        std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->consumed = false;
        
        // 检查队列是否已满
        if (m_audio_queue.size() >= AUDIO_QUEUE_SIZE - 1) {
            // 队列快满了，丢弃最旧的数据以降低延迟
            std::unique_ptr<AudioBlock> discard;
            if (m_audio_queue.pop(discard)) {
                ReleaseBlock(std::move(discard));
            }
        }
        
        if (!m_audio_queue.push(std::move(block))) {
            // 队列满，标记需要重启
            m_needs_restart.store(true);
            return false;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
    }
    
    return true;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load()) return 0;
    
    int32_t total_frames = 0;
    
    // 当前块中的帧数
    if (m_current_block && !m_current_block->consumed) {
        size_t bytes_remaining = m_current_block->available();
        size_t bytes_per_frame = GetBytesPerSample(m_sample_format.load()) * m_channel_count.load();
        total_frames += static_cast<int32_t>(bytes_remaining / bytes_per_frame);
    }
    
    // 队列中的帧数
    uint32_t queue_size = m_audio_queue.size();
    size_t bytes_per_frame = GetBytesPerSample(m_sample_format.load()) * m_channel_count.load();
    int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / bytes_per_frame);
    total_frames += queue_size * frames_per_block;
    
    return total_frames;
}

void OboeAudioRenderer::SetVolume(float volume) {
    // Oboe 没有直接的 setVolume 方法，我们在回调中应用音量
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    ClearAllBuffers();
    
    // 重新启动流
    if (m_stream) {
        m_stream->stop();
        m_stream->close();
        m_stream.reset();
    }
    
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    
    ConfigureAndOpenStream();
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        default:         return oboe::AudioFormat::I16;
    }
}

size_t OboeAudioRenderer::GetBytesPerSample(int32_t format) {
    switch (format) {
        case PCM_INT16:  return 2;
        case PCM_INT24:  return 3;
        case PCM_INT32:  return 4;
        case PCM_FLOAT:  return 4;
        default:         return 2;
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReady(oboe::AudioStream* audioStream, 
                                                         void* audioData, 
                                                         int32_t num_frames) {
    if (!m_initialized.load() || !audioStream || !audioData) {
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t device_channels = m_stream->getChannelCount();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_needed = num_frames * device_channels * bytes_per_sample;
    
    // 清空输出缓冲区
    std::memset(audioData, 0, bytes_needed);
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_copied = 0;
    
    float volume = m_volume.load();
    
    while (bytes_copied < bytes_needed) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                ReleaseBlock(std::move(m_current_block));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 没有数据，记录下溢
                m_consecutive_underruns++;
                
                // 如果连续多次下溢，标记需要重启
                if (m_consecutive_underruns.load() > 10) {
                    m_needs_restart.store(true);
                }
                
                // 保持静音
                break;
            }
            
            // 重置连续下溢计数
            m_consecutive_underruns.store(0);
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_needed - bytes_copied);
        
        if (bytes_to_copy == 0) {
            break;
        }
        
        // 复制数据（让Oboe自动处理格式转换）
        std::memcpy(output + bytes_copied, 
                   m_current_block->data + m_current_block->data_played,
                   bytes_to_copy);
        
        // 应用音量
        if (volume != 1.0f) {
            ApplyVolumeToBuffer(output + bytes_copied, bytes_to_copy, volume);
        }
        
        bytes_copied += bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    // 检查是否太久没有写入数据
    auto now = std::chrono::steady_clock::now();
    auto time_since_write = std::chrono::duration_cast<std::chrono::milliseconds>(now - m_last_write_time).count();
    
    if (time_since_write > 1000 && m_stream_started.load()) {
        // 超过1秒没有写入数据，暂停流以节省资源
        std::lock_guard<std::mutex> lock(m_stream_mutex);
        if (m_stream && m_stream_started.load()) {
            m_stream->pause();
            m_stream_started.store(false);
        }
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::ApplyVolumeToBuffer(uint8_t* buffer, size_t bytes, float volume) {
    // 根据当前样本格式应用音量
    switch (m_sample_format.load()) {
        case PCM_INT16: {
            int16_t* samples = reinterpret_cast<int16_t*>(buffer);
            size_t sample_count = bytes / sizeof(int16_t);
            for (size_t i = 0; i < sample_count; ++i) {
                samples[i] = static_cast<int16_t>(samples[i] * volume);
            }
            break;
        }
        case PCM_FLOAT: {
            float* samples = reinterpret_cast<float*>(buffer);
            size_t sample_count = bytes / sizeof(float);
            for (size_t i = 0; i < sample_count; ++i) {
                samples[i] = samples[i] * volume;
            }
            break;
        }
        case PCM_INT32: {
            int32_t* samples = reinterpret_cast<int32_t*>(buffer);
            size_t sample_count = bytes / sizeof(int32_t);
            for (size_t i = 0; i < sample_count; ++i) {
                samples[i] = static_cast<int32_t>(samples[i] * volume);
            }
            break;
        }
        default:
            // 对于其他格式，不应用音量
            break;
    }
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    // 标记需要重启流
    m_needs_restart.store(true);
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_stream_started.store(false);
    m_needs_restart.store(true);
}

oboe::DataCallbackResult OboeAudioRenderer::SimpleAudioCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    return m_renderer->OnAudioReady(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::SimpleErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::SimpleErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe