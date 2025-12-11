#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <sstream>
#include <iomanip>
#include <dlfcn.h>
#include <unistd.h>
#include <sys/syscall.h>
#include <android/log.h>

#define LOG_TAG "RyujinxOboe"
#define LOGV(...) __android_log_print(ANDROID_LOG_VERBOSE, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace RyujinxOboe {

// 静态成员初始化
std::mutex OboeAudioRenderer::s_instances_mutex;
std::set<OboeAudioRenderer*> OboeAudioRenderer::s_active_instances;

// ADPF函数指针类型
typedef int (*AdpfCreateFn)(pid_t, int64_t);
typedef void (*AdpfCloseFn)(void*);
typedef void (*AdpfBeginFn)(void*);
typedef void (*AdpfEndFn)(void*, double);

// ADPF包装器实现
bool OboeAudioRenderer::AdpfWrapper::open(pid_t tid, int64_t targetDurationNanos) {
#ifdef __ANDROID__
    void* lib = dlopen("libadpf.so", RTLD_NOW);
    if (!lib) {
        lib = dlopen("libadpf_ndk.so", RTLD_NOW);
    }
    
    if (lib) {
        AdpfCreateFn create = reinterpret_cast<AdpfCreateFn>(dlsym(lib, "APerformanceHint_createSession"));
        if (create) {
            int result = create(tid, targetDurationNanos);
            if (result >= 0) {
                handle = reinterpret_cast<void*>(result);
                isOpen = true;
                attempted = true;
                LOGI("ADPF session created successfully");
                return true;
            }
        }
        dlclose(lib);
    }
#endif
    attempted = true;
    LOGW("ADPF not available on this device");
    return false;
}

void OboeAudioRenderer::AdpfWrapper::close() {
#ifdef __ANDROID__
    if (handle) {
        void* lib = dlopen("libadpf.so", RTLD_NOW);
        if (!lib) {
            lib = dlopen("libadpf_ndk.so", RTLD_NOW);
        }
        
        if (lib) {
            AdpfCloseFn closeFn = reinterpret_cast<AdpfCloseFn>(dlsym(lib, "APerformanceHint_closeSession"));
            if (closeFn) {
                closeFn(handle);
            }
            dlclose(lib);
        }
        handle = nullptr;
        isOpen = false;
    }
#endif
}

void OboeAudioRenderer::AdpfWrapper::onBeginCallback() {
#ifdef __ANDROID__
    if (handle && isOpen) {
        void* lib = dlopen("libadpf.so", RTLD_NOW);
        if (!lib) {
            lib = dlopen("libadpf_ndk.so", RTLD_NOW);
        }
        
        if (lib) {
            AdpfBeginFn beginFn = reinterpret_cast<AdpfBeginFn>(dlsym(lib, "APerformanceHint_reportActualWorkDuration"));
            if (beginFn) {
                beginFn(handle);
            }
            dlclose(lib);
        }
    }
#endif
}

void OboeAudioRenderer::AdpfWrapper::onEndCallback(double durationScaler) {
#ifdef __ANDROID__
    if (handle && isOpen) {
        void* lib = dlopen("libadpf.so", RTLD_NOW);
        if (!lib) {
            lib = dlopen("libadpf_ndk.so", RTLD_NOW);
        }
        
        if (lib) {
            AdpfEndFn endFn = reinterpret_cast<AdpfEndFn>(dlsym(lib, "APerformanceHint_updateTargetWorkDuration"));
            if (endFn) {
                endFn(handle, durationScaler);
            }
            dlclose(lib);
        }
    }
#endif
}

OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
    PreallocateBlocks(256);
    RegisterInstance();
    UpdateState(StreamState::Uninitialized);
}

OboeAudioRenderer::~OboeAudioRenderer() {
    UnregisterInstance();
    Shutdown();
    m_adpf_wrapper.close();
}

void OboeAudioRenderer::RegisterInstance() {
    std::lock_guard<std::mutex> lock(s_instances_mutex);
    s_active_instances.insert(this);
    LOGD("OboeAudioRenderer instance created. Total instances: %zu", s_active_instances.size());
}

void OboeAudioRenderer::UnregisterInstance() {
    std::lock_guard<std::mutex> lock(s_instances_mutex);
    s_active_instances.erase(this);
    LOGD("OboeAudioRenderer instance destroyed. Remaining instances: %zu", s_active_instances.size());
}

size_t OboeAudioRenderer::GetActiveInstanceCount() {
    std::lock_guard<std::mutex> lock(s_instances_mutex);
    return s_active_instances.size();
}

void OboeAudioRenderer::DumpAllInstancesInfo() {
    std::lock_guard<std::mutex> lock(s_instances_mutex);
    LOGI("=== OboeAudioRenderer Instances Dump ===");
    LOGI("Total instances: %zu", s_active_instances.size());
    
    int i = 0;
    for (auto* instance : s_active_instances) {
        LOGI("Instance %d:", i++);
        LOGI("  Initialized: %s", instance->m_initialized.load() ? "Yes" : "No");
        LOGI("  State: %d", static_cast<int>(instance->m_current_state.load()));
        LOGI("  Sample Rate: %d", instance->m_sample_rate.load());
        LOGI("  Channels: %d", instance->m_channel_count.load());
        LOGI("  Volume: %.2f", instance->m_volume.load());
    }
    LOGI("=== End Dump ===");
}

void OboeAudioRenderer::UpdateState(StreamState newState) {
    StreamState oldState = m_current_state.load();
    m_current_state.store(newState);
    
    if (m_state_callback_user) {
        m_state_callback_user(oldState, newState);
    }
    
    LOGD("Stream state changed: %d -> %d", 
         static_cast<int>(oldState), 
         static_cast<int>(newState));
}

void OboeAudioRenderer::HandleError(oboe::Result error, const std::string& context) {
    std::lock_guard<std::mutex> lock(m_stats_mutex);
    m_performance_stats.error_count++;
    m_performance_stats.last_error_time = std::chrono::steady_clock::now();
    
    std::string errorMsg = context + ": " + oboe::convertToText(error);
    LOGE("%s", errorMsg.c_str());
    
    if (m_error_callback_user) {
        m_error_callback_user(errorMsg, error);
    }
    
    UpdateState(StreamState::Error);
}

bool OboeAudioRenderer::RecoverFromError(oboe::Result error) {
    LOGW("Attempting to recover from error: %s", oboe::convertToText(error));
    
    switch (error) {
        case oboe::Result::ErrorDisconnected:
            // 设备断开，等待100ms后重连
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            break;
            
        case oboe::Result::ErrorInvalidState:
            // 无效状态，完全重置
            Reset();
            return true;
            
        case oboe::Result::ErrorUnavailable:
            // AAudio不可用，尝试降级到OpenSL ES
            if (m_current_api == oboe::AudioApi::AAudio) {
                LOGW("AAudio unavailable, attempting OpenSL ES fallback");
                m_current_api = oboe::AudioApi::OpenSLES;
                return true;
            }
            break;
            
        case oboe::Result::ErrorInternal:
            // 内部错误，可能是权限问题
            LOGW("Internal error, checking permissions");
            break;
            
        default:
            break;
    }
    
    // 通用恢复：关闭并重新打开流
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    CloseStream();
    if (ConfigureAndOpenStream()) {
        UpdateState(StreamState::Started);
        return true;
    }
    
    return false;
}

bool OboeAudioRenderer::TryFallbackApi() {
    LOGD("Attempting API fallback");
    
    if (m_current_api == oboe::AudioApi::AAudio) {
        m_current_api = oboe::AudioApi::OpenSLES;
        LOGD("Falling back to OpenSL ES");
        return true;
    }
    
    return false;
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
    
    UpdateState(StreamState::Opening);
    
    // 首选AAudio，性能更好
    m_current_api = oboe::AudioApi::AAudio;
    
    if (!TryOpenStreamWithRetry(3)) {
        HandleError(oboe::Result::ErrorInternal, "Failed to open audio stream");
        UpdateState(StreamState::Error);
        return false;
    }
    
    m_initialized.store(true);
    UpdateState(StreamState::Started);
    
    LOGI("OboeAudioRenderer initialized: %dHz, %d channels, format %d", 
         sampleRate, channelCount, sampleFormat);
    
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    UpdateState(StreamState::Stopping);
    CloseStream();
    m_audio_queue.clear();
    m_current_block.reset();
    m_initialized.store(false);
    m_stream_started.store(false);
    UpdateState(StreamState::Closed);
    
    LOGD("OboeAudioRenderer shutdown complete");
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game)
           ->setFramesPerCallback(240);
    
    auto channel_count = m_channel_count.load();
    auto channel_mask = [&]() {
        switch (channel_count) {
        case 1: return oboe::ChannelMask::Mono;
        case 2: return oboe::ChannelMask::Stereo;
        case 6: return oboe::ChannelMask::CM5Point1;
        default: return oboe::ChannelMask::Unspecified;
        }
    }();
    
    builder.setChannelCount(channel_count)
           ->setChannelMask(channel_mask)
           ->setChannelConversionAllowed(true);
}

void OboeAudioRenderer::ConfigureForOpenSLES(oboe::AudioStreamBuilder& builder) {
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::OpenSLES)
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setFormat(m_oboe_format)
           ->setUsage(oboe::Usage::Game);
    
    builder.setChannelCount(m_channel_count.load())
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::TryOpenStreamWithRetry(int maxRetryCount) {
    for (int attempt = 0; attempt < maxRetryCount; ++attempt) {
        if (ConfigureAndOpenStream()) {
            return true;
        }
        
        if (attempt < maxRetryCount - 1) {
            int delay = 50 * (1 << attempt); // 指数退避：50, 100, 200ms
            LOGD("Open stream failed, retry %d/%d after %dms", 
                 attempt + 1, maxRetryCount, delay);
            std::this_thread::sleep_for(std::chrono::milliseconds(delay));
        }
    }
    return false;
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    if (m_current_api == oboe::AudioApi::AAudio) {
        ConfigureForAAudioExclusive(builder);
    } else {
        ConfigureForOpenSLES(builder);
    }
    
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        // 如果独占模式失败，尝试共享模式
        if (m_current_api == oboe::AudioApi::AAudio) {
            LOGW("AAudio exclusive mode failed, trying shared mode");
            builder.setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
        }
        
        if (result != oboe::Result::OK) {
            // 如果AAudio失败，尝试OpenSL ES
            if (m_current_api == oboe::AudioApi::AAudio) {
                LOGW("AAudio failed, trying OpenSL ES");
                m_current_api = oboe::AudioApi::OpenSLES;
                ConfigureForOpenSLES(builder);
                result = builder.openStream(m_stream);
            }
            
            if (result != oboe::Result::OK) {
                HandleError(result, "Failed to open audio stream");
                return false;
            }
        }
    }
    
    if (!OptimizeBufferSize()) {
        CloseStream();
        return false;
    }
    
    m_device_channels = m_stream->getChannelCount();
    
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        HandleError(result, "Failed to start audio stream");
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    
    LOGI("Stream opened successfully:");
    LOGI("  API: %s", oboe::convertToText(m_stream->getAudioApi()));
    LOGI("  Sample Rate: %d", m_stream->getSampleRate());
    LOGI("  Channels: %d", m_stream->getChannelCount());
    LOGI("  Format: %s", oboe::convertToText(m_stream->getFormat()));
    LOGI("  Buffer Size: %d frames", m_stream->getBufferSizeInFrames());
    LOGI("  Frames Per Burst: %d", m_stream->getFramesPerBurst());
    LOGI("  Performance Mode: %s", oboe::convertToText(m_stream->getPerformanceMode()));
    
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) return false;
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    int32_t sampleRate = m_stream->getSampleRate();
    
    // 根据设备和采样率优化缓冲区大小
    int32_t desired_buffer_size = framesPerBurst;
    
    // 针对不同场景调整
    if (m_current_api == oboe::AudioApi::AAudio) {
        // AAudio：低延迟模式
        if (sampleRate <= 48000) {
            desired_buffer_size = framesPerBurst * 2; // 标准低延迟
        } else {
            desired_buffer_size = framesPerBurst * 4; // 高采样率需要更大缓冲区
        }
    } else {
        // OpenSL ES：需要更大的缓冲区
        desired_buffer_size = framesPerBurst * 4;
    }
    
    // 设置合理的限制
    desired_buffer_size = std::max(desired_buffer_size, framesPerBurst);
    desired_buffer_size = std::min(desired_buffer_size, framesPerBurst * 8);
    
    auto result = m_stream->setBufferSizeInFrames(desired_buffer_size);
    if (result != oboe::Result::OK) {
        LOGW("Failed to set buffer size to %d, using default", desired_buffer_size);
        // 使用默认值
        desired_buffer_size = framesPerBurst * 2;
        m_stream->setBufferSizeInFrames(desired_buffer_size);
    }
    
    LOGD("Buffer size optimized to %d frames (%.1f ms)", 
         desired_buffer_size, 
         (desired_buffer_size * 1000.0) / sampleRate);
    
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
        m_adpf_wrapper.close();
    }
}

void OboeAudioRenderer::BeginPerformanceHint() {
    if (m_performance_hint_enabled && !m_adpf_wrapper.attempted) {
        if (m_stream) {
            int64_t targetDurationNanos = (m_stream->getFramesPerBurst() * 1e9) / m_stream->getSampleRate();
            pid_t tid = static_cast<pid_t>(syscall(__NR_gettid));
            m_adpf_wrapper.open(tid, targetDurationNanos);
        }
    }
    
    if (m_adpf_wrapper.isOpen) {
        m_adpf_wrapper.onBeginCallback();
    }
}

void OboeAudioRenderer::EndPerformanceHint(int32_t num_frames) {
    if (m_adpf_wrapper.isOpen && m_stream) {
        double durationScaler = static_cast<double>(m_stream->getFramesPerBurst()) / num_frames;
        if (durationScaler >= 0.1 && durationScaler <= 10.0) { // 合理的范围
            m_adpf_wrapper.onEndCallback(durationScaler);
        }
    }
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        LOGE("WriteAudio: Invalid parameters or not initialized");
        return false;
    }
    
    int32_t system_channels = m_channel_count.load();
    size_t data_size = num_frames * system_channels * sizeof(int16_t);
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        LOGE("WriteAudioRaw: Invalid parameters or not initialized");
        return false;
    }
    
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t total_bytes = num_frames * system_channels * bytes_per_sample;
    
    const uint8_t* byte_data = static_cast<const uint8_t*>(data);
    size_t bytes_remaining = total_bytes;
    size_t bytes_processed = 0;
    
    m_performance_stats.total_frames_written += num_frames;
    
    while (bytes_remaining > 0) {
        auto block = m_object_pool.acquire();
        if (!block) {
            LOGE("Failed to acquire audio block from pool");
            return false;
        }
        
        size_t copy_size = std::min(bytes_remaining, AudioBlock::BLOCK_SIZE);
        
        if (sampleFormat == PCM_INT16) {
            const int16_t* src = reinterpret_cast<const int16_t*>(byte_data + bytes_processed);
            int16_t* dst = reinterpret_cast<int16_t*>(block->data);
            std::memcpy(dst, src, copy_size);
        } else if (sampleFormat == PCM_FLOAT) {
            const float* src = reinterpret_cast<const float*>(byte_data + bytes_processed);
            float* dst = reinterpret_cast<float*>(block->data);
            std::memcpy(dst, src, copy_size);
        } else if (sampleFormat == PCM_INT32) {
            const int32_t* src = reinterpret_cast<const int32_t*>(byte_data + bytes_processed);
            int32_t* dst = reinterpret_cast<int32_t*>(block->data);
            std::memcpy(dst, src, copy_size);
        } else {
            std::memcpy(block->data, byte_data + bytes_processed, copy_size);
        }
        
        block->data_size = copy_size;
        block->data_played = 0;
        block->sample_format = sampleFormat;
        block->consumed = false;
        
        if (!m_audio_queue.push(std::move(block))) {
            LOGE("Audio queue is full, dropping %zu bytes", copy_size);
            // 队列满时丢弃数据，但记录XRun
            m_performance_stats.xrun_count++;
            return false;
        }
        
        bytes_processed += copy_size;
        bytes_remaining -= copy_size;
    }
    
    return true;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_initialized.load() || !m_stream) return 0;
    
    int32_t total_frames = 0;
    int32_t device_channels = m_device_channels;
    
    if (m_current_block && !m_current_block->consumed) {
        size_t bytes_remaining = m_current_block->available();
        size_t bytes_per_sample = GetBytesPerSample(m_current_block->sample_format);
        total_frames += static_cast<int32_t>(bytes_remaining / (device_channels * bytes_per_sample));
    }
    
    uint32_t queue_size = m_audio_queue.size();
    size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    int32_t frames_per_block = static_cast<int32_t>(AudioBlock::BLOCK_SIZE / (device_channels * bytes_per_sample));
    total_frames += queue_size * frames_per_block;
    
    return total_frames;
}

PerformanceStats OboeAudioRenderer::GetPerformanceStats() const {
    std::lock_guard<std::mutex> lock(m_stats_mutex);
    // 使用自定义拷贝构造函数返回副本
    return PerformanceStats(m_performance_stats);
}

double OboeAudioRenderer::CalculateLatencyMillis() {
    if (!m_stream || m_current_state.load() != StreamState::Started) {
        return 0.0;
    }
    
    try {
        // 获取硬件时间戳
        int64_t hardwareFrameIndex;
        int64_t hardwareFrameHardwareTime;
        auto result = m_stream->getTimestamp(CLOCK_MONOTONIC,
                                           &hardwareFrameIndex,
                                           &hardwareFrameHardwareTime);
        
        if (result != oboe::Result::OK) {
            return 0.0;
        }
        
        // 获取应用帧位置
        int64_t appFrameIndex = m_stream->getFramesWritten();
        
        // 计算当前时间
        auto now = std::chrono::steady_clock::now();
        int64_t appFrameAppTime = std::chrono::duration_cast<std::chrono::nanoseconds>(
            now.time_since_epoch()).count();
        
        // 计算帧数差和时间差
        int64_t frameIndexDelta = appFrameIndex - hardwareFrameIndex;
        int64_t frameTimeDelta = (frameIndexDelta * 1000000000LL) / m_stream->getSampleRate();
        int64_t appFrameHardwareTime = hardwareFrameHardwareTime + frameTimeDelta;
        
        // 计算延迟
        double latencyNanos = static_cast<double>(appFrameHardwareTime - appFrameAppTime);
        double latencyMillis = latencyNanos / 1000000.0;
        
        // 使用原子操作更新统计，无需锁
        double currentAvg = m_performance_stats.average_latency_ms.load();
        m_performance_stats.average_latency_ms.store(
            (currentAvg * 0.9) + (latencyMillis * 0.1)
        );
        
        double currentMax = m_performance_stats.max_latency_ms.load();
        if (latencyMillis > currentMax) {
            m_performance_stats.max_latency_ms.store(latencyMillis);
        }
        
        double currentMin = m_performance_stats.min_latency_ms.load();
        if (latencyMillis < currentMin) {
            m_performance_stats.min_latency_ms.store(latencyMillis);
        }
        
        return latencyMillis;
        
    } catch (...) {
        return 0.0;
    }
}

int32_t OboeAudioRenderer::GetXRunCount() {
    if (!m_stream) return 0;
    
    try {
        auto result = m_stream->getXRunCount();
        if (result) {
            // 直接使用原子操作，无需锁
            m_performance_stats.xrun_count.store(result.value());
            return result.value();
        }
    } catch (...) {
        // 忽略异常
    }
    
    return 0;
}

void OboeAudioRenderer::UpdateLatencyMeasurement() {
    CalculateLatencyMillis();
}

void OboeAudioRenderer::UpdateXRunCount() {
    GetXRunCount();
}

void OboeAudioRenderer::SetVolume(float volume) {
    float clampedVolume = std::max(0.0f, std::min(volume, 1.0f));
    m_volume.store(clampedVolume);
    
    if (m_stream) {
        // 注意：Oboe没有直接的音量设置API，需要在回调中应用
        LOGD("Volume set to %.2f (applied in audio callback)", clampedVolume);
    }
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    LOGD("Resetting audio renderer");
    
    m_audio_queue.clear();
    if (m_current_block) {
        m_object_pool.release(std::move(m_current_block));
    }
    
    std::lock_guard<std::mutex> stats_lock(m_stats_mutex);
    // 重置性能统计
    m_performance_stats.xrun_count.store(0);
    m_performance_stats.total_frames_played.store(0);
    m_performance_stats.total_frames_written.store(0);
    m_performance_stats.average_latency_ms.store(0.0);
    m_performance_stats.max_latency_ms.store(0.0);
    m_performance_stats.min_latency_ms.store(1000.0);
    m_performance_stats.error_count.store(0);
    m_performance_stats.last_error_time = std::chrono::steady_clock::time_point();
    
    CloseStream();
    
    if (m_initialized.load()) {
        ConfigureAndOpenStream();
        UpdateState(StreamState::Started);
    } else {
        UpdateState(StreamState::Uninitialized);
    }
}

void OboeAudioRenderer::PreallocateBlocks(size_t count) {
    m_object_pool.preallocate(count);
    LOGD("Preallocated %zu audio blocks", count);
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

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    if (!m_initialized.load() || !audioStream) {
        int32_t channels = m_device_channels;
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        size_t bytes_requested = num_frames * channels * bytes_per_sample;
        std::memset(audioData, 0, bytes_requested);
        return oboe::DataCallbackResult::Continue;
    }
    
    // 开始性能提示
    BeginPerformanceHint();
    
    // 更新性能统计
    m_performance_stats.total_frames_played += num_frames;
    
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_remaining = num_frames * m_device_channels * GetBytesPerSample(m_sample_format.load());
    size_t bytes_copied = 0;
    bool underrun = false;
    
    while (bytes_remaining > 0) {
        if (!m_current_block || m_current_block->consumed || m_current_block->available() == 0) {
            if (m_current_block) {
                m_object_pool.release(std::move(m_current_block));
            }
            
            if (!m_audio_queue.pop(m_current_block)) {
                // 缓冲区不足，产生静音
                std::memset(output + bytes_copied, 0, bytes_remaining);
                underrun = true;
                break;
            }
        }
        
        size_t bytes_to_copy = std::min(m_current_block->available(), bytes_remaining);
        std::memcpy(output + bytes_copied, 
                   m_current_block->data + m_current_block->data_played,
                   bytes_to_copy);
        
        // 应用音量
        if (m_volume.load() < 1.0f) {
            if (m_current_block->sample_format == PCM_INT16) {
                int16_t* samples = reinterpret_cast<int16_t*>(output + bytes_copied);
                size_t sample_count = bytes_to_copy / sizeof(int16_t);
                for (size_t i = 0; i < sample_count; i++) {
                    samples[i] = static_cast<int16_t>(samples[i] * m_volume.load());
                }
            } else if (m_current_block->sample_format == PCM_FLOAT) {
                float* samples = reinterpret_cast<float*>(output + bytes_copied);
                size_t sample_count = bytes_to_copy / sizeof(float);
                for (size_t i = 0; i < sample_count; i++) {
                    samples[i] *= m_volume.load();
                }
            }
        }
        
        bytes_copied += bytes_to_copy;
        bytes_remaining -= bytes_to_copy;
        m_current_block->data_played += bytes_to_copy;
        
        if (m_current_block->available() == 0) {
            m_current_block->consumed = true;
        }
    }
    
    // 结束性能提示
    EndPerformanceHint(num_frames);
    
    // 记录underrun
    if (underrun) {
        m_performance_stats.xrun_count++;
        LOGW("Audio underrun: %d frames of silence inserted", num_frames);
    }
    
    // 定期更新延迟和XRun统计
    static int callback_count = 0;
    if (++callback_count % 100 == 0) { // 每100次回调更新一次
        UpdateLatencyMeasurement();
        UpdateXRunCount();
    }
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Stream error after close: %s", oboe::convertToText(error));
    
    HandleError(error, "Stream error after close");
    
    if (!RecoverFromError(error)) {
        std::lock_guard<std::mutex> lock(m_stream_mutex);
        if (m_initialized.load()) {
            CloseStream();
            if (TryFallbackApi()) {
                ConfigureAndOpenStream();
            }
        }
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGW("Stream error before close: %s", oboe::convertToText(error));
    
    HandleError(error, "Stream error before close");
    m_stream_started.store(false);
    UpdateState(StreamState::Error);
}

oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    return m_renderer->OnAudioReadyMultiFormat(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorAfterClose(
    oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorBeforeClose(
    oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

} // namespace RyujinxOboe
