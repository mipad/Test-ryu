// oboe_audio_renderer.cpp (支持所有采样率，带内存池优化和对齐保证)
#include "oboe_audio_renderer.h"
#include "stabilized_audio_callback.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>
#include <array>
#include <android/log.h>

namespace RyujinxOboe {

// =============== AudioMemoryPool Implementation ===============
OboeAudioRenderer::AudioMemoryPool::AudioMemoryPool(size_t pool_size) {
    m_blocks.resize(pool_size);
    for (auto& block : m_blocks) {
        m_free_blocks.push_back(&block);
        
        // 验证对齐
        if (!block.isDataAligned()) {
            __android_log_print(ANDROID_LOG_WARN, "AudioMemoryPool", 
                               "Memory block not properly aligned: %p", block.data);
        }
    }
    
    __android_log_print(ANDROID_LOG_INFO, "AudioMemoryPool", 
                       "Created pool with %zu blocks, alignment=%zu", pool_size, ALIGNMENT);
}

OboeAudioRenderer::AudioMemoryPool::~AudioMemoryPool() {
    Clear();
}

AudioMemoryBlock* OboeAudioRenderer::AudioMemoryPool::AllocateBlock() {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    
    if (m_free_blocks.empty()) {
        // 池耗尽，返回nullptr
        return nullptr;
    }
    
    AudioMemoryBlock* block = m_free_blocks.back();
    m_free_blocks.pop_back();
    
    block->in_use = true;
    block->used_size = 0;
    
    return block;
}

void OboeAudioRenderer::AudioMemoryPool::ReleaseBlock(AudioMemoryBlock* block) {
    if (!block) return;
    
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    
    block->in_use = false;
    block->used_size = 0;
    m_free_blocks.push_back(block);
}

void OboeAudioRenderer::AudioMemoryPool::Clear() {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    
    m_free_blocks.clear();
    for (auto& block : m_blocks) {
        block.in_use = false;
        block.used_size = 0;
        m_free_blocks.push_back(&block);
    }
}

size_t OboeAudioRenderer::AudioMemoryPool::GetFreeBlockCount() const {
    std::lock_guard<std::mutex> lock(m_pool_mutex);
    return m_free_blocks.size();
}

// =============== PooledAudioBufferQueue Implementation ===============
OboeAudioRenderer::PooledAudioBufferQueue::PooledAudioBufferQueue(std::shared_ptr<AudioMemoryPool> pool, size_t max_buffers) 
    : m_max_buffers(max_buffers), m_memory_pool(pool) {
}

OboeAudioRenderer::PooledAudioBufferQueue::~PooledAudioBufferQueue() {
    Clear();
}

void OboeAudioRenderer::PooledAudioBufferQueue::CopyAlignedMemory(void* dst, const void* src, size_t size) {
    if (size == 0) return;
    
    // 检查是否可以使用对齐拷贝
    bool src_aligned = AlignedMemory::IsAligned(src);
    bool dst_aligned = AlignedMemory::IsAligned(dst);
    bool size_aligned = (size % sizeof(uint32_t)) == 0;
    
    if (src_aligned && dst_aligned && size_aligned) {
        // 使用32位对齐拷贝（比memcpy更快）
        const uint32_t* src32 = static_cast<const uint32_t*>(src);
        uint32_t* dst32 = static_cast<uint32_t*>(dst);
        size_t count = size / sizeof(uint32_t);
        
        for (size_t i = 0; i < count; ++i) {
            dst32[i] = src32[i];
        }
    } else {
        // 回退到标准memcpy
        std::memcpy(dst, src, size);
    }
}

bool OboeAudioRenderer::PooledAudioBufferQueue::WriteRaw(const uint8_t* data, size_t data_size, int32_t sample_format) {
    if (!data || data_size == 0 || data_size > MAX_AUDIO_FRAME_SIZE) return false;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 检查队列是否已满
    if (m_buffers.size() >= m_max_buffers) {
        return false;
    }
    
    // 从内存池分配块
    AudioMemoryBlock* block = m_memory_pool->AllocateBlock();
    if (!block) {
        return false; // 内存池耗尽
    }
    
    // 使用对齐的内存拷贝
    CopyAlignedMemory(block->data, data, data_size);
    block->used_size = data_size;
    block->sample_format = sample_format;
    
    // 创建缓冲区
    PooledAudioBuffer buffer;
    buffer.block = block;
    buffer.data_played = 0;
    buffer.consumed = false;
    
    m_buffers.push(std::move(buffer));
    m_current_format = sample_format;
    
    return true;
}

size_t OboeAudioRenderer::PooledAudioBufferQueue::ReadRaw(uint8_t* output, size_t output_size, int32_t target_format) {
    if (!output || output_size == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t bytes_written = 0;
    
    while (bytes_written < output_size) {
        // 如果当前播放缓冲区已消费或为空，从队列获取新缓冲区
        if (m_playing_buffer.consumed || !m_playing_buffer.block) {
            if (m_buffers.empty()) {
                break; // 没有更多数据
            }
            
            m_playing_buffer = std::move(m_buffers.front());
            m_buffers.pop();
        }
        
        // 计算当前缓冲区可用的数据
        size_t bytes_available = m_playing_buffer.block->used_size - m_playing_buffer.data_played;
        size_t bytes_to_copy = std::min(bytes_available, output_size - bytes_written);
        
        // 使用对齐的内存拷贝
        CopyAlignedMemory(output + bytes_written, 
                         m_playing_buffer.block->data + m_playing_buffer.data_played,
                         bytes_to_copy);
        
        bytes_written += bytes_to_copy;
        m_playing_buffer.data_played += bytes_to_copy;
        
        // 检查当前缓冲区是否已完全消费
        if (m_playing_buffer.data_played >= m_playing_buffer.block->used_size) {
            m_playing_buffer.consumed = true;
            // 释放内存块回池
            if (m_playing_buffer.block) {
                m_memory_pool->ReleaseBlock(m_playing_buffer.block);
                m_playing_buffer.block = nullptr;
            }
        }
    }
    
    return bytes_written;
}

size_t OboeAudioRenderer::PooledAudioBufferQueue::Available() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t total_bytes = 0;
    
    // 计算队列中所有缓冲区的总字节数
    std::queue<PooledAudioBuffer> temp_queue = m_buffers;
    while (!temp_queue.empty()) {
        const auto& buffer = temp_queue.front();
        if (buffer.block) {
            total_bytes += buffer.block->used_size;
        }
        temp_queue.pop();
    }
    
    // 加上当前播放缓冲区剩余的字节数
    if (!m_playing_buffer.consumed && m_playing_buffer.block) {
        total_bytes += (m_playing_buffer.block->used_size - m_playing_buffer.data_played);
    }
    
    return total_bytes;
}

void OboeAudioRenderer::PooledAudioBufferQueue::Clear() {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 释放队列中所有缓冲区的内存块
    while (!m_buffers.empty()) {
        auto& buffer = m_buffers.front();
        if (buffer.block) {
            m_memory_pool->ReleaseBlock(buffer.block);
        }
        m_buffers.pop();
    }
    
    // 释放当前播放缓冲区的内存块
    if (m_playing_buffer.block) {
        m_memory_pool->ReleaseBlock(m_playing_buffer.block);
        m_playing_buffer.block = nullptr;
    }
    
    m_playing_buffer = PooledAudioBuffer{};
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReadyMultiFormat(audioStream, audioData, num_frames);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    // 先创建内存池
    m_memory_pool = std::make_shared<AudioMemoryPool>();
    
    m_audio_callback = std::make_shared<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_shared<OboeErrorCallback>(this);
    
    // 默认创建稳定回调 - 使用新的构造函数
    m_stabilized_callback = std::make_shared<StabilizedAudioCallback>(m_audio_callback, m_error_callback);
    m_stabilized_callback->setEnabled(true);
    m_stabilized_callback->setLoadIntensity(0.1f); // 降低默认强度
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                       "OboeAudioRenderer created with %zu-byte alignment", ALIGNMENT);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::GetInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

void OboeAudioRenderer::FillSilenceAligned(void* data, size_t size_in_bytes) {
    if (!data || size_in_bytes == 0) return;
    
    // 检查是否可以使用对齐填充
    bool data_aligned = AlignedMemory::IsAligned(data);
    bool size_aligned = (size_in_bytes % sizeof(uint32_t)) == 0;
    
    if (data_aligned && size_aligned) {
        // 使用32位对齐填充
        uint32_t* data32 = static_cast<uint32_t*>(data);
        size_t count = size_in_bytes / sizeof(uint32_t);
        for (size_t i = 0; i < count; ++i) {
            data32[i] = 0;
        }
    } else {
        // 回退到标准memset
        std::memset(data, 0, size_in_bytes);
    }
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
    
    // 使用基于内存池的样本缓冲区队列
    m_raw_sample_queue = std::make_unique<PooledAudioBufferQueue>(m_memory_pool, 32);
    
    // 验证内存池对齐
    auto test_block = m_memory_pool->AllocateBlock();
    if (test_block) {
        if (!test_block->isDataAligned()) {
            __android_log_print(ANDROID_LOG_WARN, "OboeAudioRenderer", 
                               "Memory pool block not aligned: %p", test_block->data);
        }
        m_memory_pool->ReleaseBlock(test_block);
    }
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                       "Initialized: %dHz, %dch, %s, alignment=%zu", 
                       sampleRate, channelCount, m_current_sample_format.c_str(), ALIGNMENT);
    
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
        m_raw_sample_queue.reset();
    }
    
    if (m_memory_pool) {
        m_memory_pool->Clear();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", "Shutdown completed");
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    // AAudio 独占模式配置
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load()) // 使用请求的采样率
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
    
    // 默认禁用稳定回调以提升性能
    bool useStabilizedCallback = m_stabilized_callback_enabled.load();
    
    if (useStabilizedCallback && m_stabilized_callback) {
        builder.setDataCallback(m_stabilized_callback.get())
               ->setErrorCallback(m_stabilized_callback.get());
    } else {
        // 使用轻量级错误回调
        auto lightweightErrorCallback = std::make_shared<OboeErrorCallback>(this);
        builder.setDataCallback(m_audio_callback.get())
               ->setErrorCallback(lightweightErrorCallback.get());
    }
    
    // 简化回退策略 - 优先AAudio
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
    
    __android_log_print(ANDROID_LOG_INFO, "OboeAudioRenderer", 
                       "Stream started: %s, %s, %d channels", 
                       m_current_audio_api.c_str(), m_current_sharing_mode.c_str(), m_device_channels);
    
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

bool OboeAudioRenderer::RestartStream() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    // 短暂延迟让系统清理
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    
    bool success = ConfigureAndOpenStream();
    
    return success;
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
    
    // 计算数据大小
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t data_size = num_frames * system_channels * bytes_per_sample;
    
    // 检查数据大小是否超过限制
    if (data_size > MAX_AUDIO_FRAME_SIZE) {
        return false;
    }
    
    // 直接写入原始数据到内存池队列
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    bool success = m_raw_sample_queue->WriteRaw(byte_data, data_size, sampleFormat);
    
    if (!success) {
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
    m_volume.store(std::max(0.0f, std::min(volume, 1.0f)));
}

void OboeAudioRenderer::SetStabilizedCallbackEnabled(bool enabled) {
    if (m_stabilized_callback_enabled.load() != enabled) {
        m_stabilized_callback_enabled.store(enabled);
        
        // 如果音频流正在运行，需要重新配置
        if (m_initialized.load() && m_stream_started.load()) {
            std::lock_guard<std::mutex> lock(m_stream_mutex);
            CloseStream();
            ConfigureAndOpenStream();
        }
    }
}

void OboeAudioRenderer::SetStabilizedCallbackIntensity(float intensity) {
    float clampedIntensity = std::max(0.0f, std::min(intensity, 1.0f));
    m_stabilized_callback_intensity.store(clampedIntensity);
    
    if (m_stabilized_callback) {
        m_stabilized_callback->setLoadIntensity(clampedIntensity);
    }
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
    }
    
    CloseStream();
    ConfigureAndOpenStream();
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT16:  return oboe::AudioFormat::I16;
        case PCM_INT24:  return oboe::AudioFormat::I24;
        case PCM_INT32:  return oboe::AudioFormat::I32;
        case PCM_FLOAT:  return oboe::AudioFormat::Float;
        default:         return oboe::AudioFormat::I16; // 默认回退
    }
}

const char* OboeAudioRenderer::GetFormatName(int32_t format) {
    switch (format) {
        case PCM_INT16:  return "PCM16";
        case PCM_INT24:  return "PCM24";
        case PCM_INT32:  return "PCM32";
        case PCM_FLOAT:  return "Float32";
        default:         return "Unknown";
    }
}

size_t OboeAudioRenderer::GetBytesPerSample(int32_t format) {
    switch (format) {
        case PCM_INT16:  return 2;
        case PCM_INT24:  return 3;
        case PCM_INT32:  return 4;
        case PCM_FLOAT:  return 4;
        default:         return 2; // 默认PCM16
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    if (!m_initialized.load() || !m_raw_sample_queue) {
        // 快速静音填充
        int32_t channels = m_device_channels;
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        size_t total_bytes = num_frames * channels * bytes_per_sample;
        
        // 使用对齐的静音填充
        FillSilenceAligned(audioData, total_bytes);
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t channels = m_device_channels;
    size_t target_bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_requested = num_frames * channels * target_bytes_per_sample;
    
    // 从内存池队列读取数据
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_read = m_raw_sample_queue->ReadRaw(output, bytes_requested, m_sample_format.load());
    
    // 如果数据不足，使用对齐的静音填充
    if (bytes_read < bytes_requested) {
        size_t bytes_remaining = bytes_requested - bytes_read;
        FillSilenceAligned(output + bytes_read, bytes_remaining);
        m_underrun_count++;
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamError(oboe::AudioStream* audioStream, oboe::Result error) {
    // 静默处理错误，不记录日志
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    // 只在设备断开时自动重启
    if (error == oboe::Result::ErrorDisconnected) {
        RestartStream();
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false);
}

} // namespace RyujinxOboe