#include "mediacodec_decoder.h"
#include <chrono>
#include <cstring>
#include <android/log.h>

#define LOG_TAG "MediaCodecDecoder"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)

namespace RyujinxMediaCodec {

MediaCodecDecoder::MediaCodecDecoder() {
    LOGD("MediaCodecDecoder created");
}

MediaCodecDecoder::~MediaCodecDecoder() {
    Release();
    LOGD("MediaCodecDecoder destroyed");
}

bool MediaCodecDecoder::Initialize(const DecoderConfig& config) {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (status_ != DecoderStatus::UNINITIALIZED) {
        LOGE("Decoder already initialized");
        return false;
    }
    
    config_ = config;
    
    if (!InitializeInternal()) {
        CleanupInternal();
        status_ = DecoderStatus::ERROR;
        return false;
    }
    
    status_ = DecoderStatus::INITIALIZED;
    LOGI("Decoder initialized: %dx%d, codec: %s", 
         config_.width, config_.height,
         MediaCodecUtils::GetMimeType(config_.codec).c_str());
    
    return true;
}

bool MediaCodecDecoder::InitializeInternal() {
    // 创建 MediaCodec 解码器
    std::string mimeType = MediaCodecUtils::GetMimeType(config_.codec);
    mediaCodec_ = AMediaCodec_createDecoderByType(mimeType.c_str());
    
    if (!mediaCodec_) {
        LOGE("Failed to create MediaCodec for %s", mimeType.c_str());
        lastError_ = "Failed to create MediaCodec";
        return false;
    }
    
    // 创建 MediaFormat
    mediaFormat_ = AMediaFormat_new();
    if (!mediaFormat_) {
        LOGE("Failed to create MediaFormat");
        lastError_ = "Failed to create MediaFormat";
        return false;
    }
    
    // 配置 MediaFormat
    if (!ConfigureCodec()) {
        LOGE("Failed to configure codec");
        lastError_ = "Failed to configure codec";
        return false;
    }
    
    return true;
}

bool MediaCodecDecoder::ConfigureCodec() {
    // 设置基本参数
    std::string mimeType = MediaCodecUtils::GetMimeType(config_.codec);
    AMediaFormat_setString(mediaFormat_, AMEDIAFORMAT_KEY_MIME, mimeType.c_str());
    AMediaFormat_setInt32(mediaFormat_, AMEDIAFORMAT_KEY_WIDTH, config_.width);
    AMediaFormat_setInt32(mediaFormat_, AMEDIAFORMAT_KEY_HEIGHT, config_.height);
    AMediaFormat_setInt32(mediaFormat_, AMEDIAFORMAT_KEY_FRAME_RATE, config_.frameRate);
    AMediaFormat_setInt32(mediaFormat_, AMEDIAFORMAT_KEY_COLOR_FORMAT, 
                          static_cast<int>(config_.colorFormat));
    
    // I 帧间隔
    AMediaFormat_setInt32(mediaFormat_, "i-frame-interval", config_.iFrameInterval);
    
    // 比特率
    if (config_.bitrate > 0) {
        AMediaFormat_setInt32(mediaFormat_, AMEDIAFORMAT_KEY_BIT_RATE, config_.bitrate);
    }
    
    // 优先级
    AMediaFormat_setInt32(mediaFormat_, "priority", 0);
    
    // 配置特定编解码器参数
    if (!ConfigureMediaFormat(mediaFormat_, config_)) {
        LOGE("Failed to configure media format for specific codec");
        return false;
    }
    
    // 配置解码器
    media_status_t status;
    if (config_.useSurface && config_.surface) {
        status = AMediaCodec_configure(mediaCodec_, mediaFormat_, 
                                      config_.surface, nullptr, 0);
    } else {
        status = AMediaCodec_configure(mediaCodec_, mediaFormat_, 
                                      nullptr, nullptr, 0);
    }
    
    if (status != AMEDIA_OK) {
        LOGE("AMediaCodec_configure failed: %s", 
             MediaCodecUtils::ErrorToString(status).c_str());
        lastError_ = "AMediaCodec_configure failed: " + MediaCodecUtils::ErrorToString(status);
        return false;
    }
    
    return true;
}

bool MediaCodecDecoder::ConfigureMediaFormat(AMediaFormat* format, const DecoderConfig& config) {
    // 基础实现，子类可以重写
    // 设置 CSD 数据
    if (!config.csd0.empty()) {
        AMediaFormat_setBuffer(format, "csd-0", 
                              config.csd0.data(), config.csd0.size());
    }
    
    if (!config.csd1.empty()) {
        AMediaFormat_setBuffer(format, "csd-1", 
                              config.csd1.data(), config.csd1.size());
    }
    
    if (!config.csd2.empty()) {
        AMediaFormat_setBuffer(format, "csd-2", 
                              config.csd2.data(), config.csd2.size());
    }
    
    return true;
}

bool MediaCodecDecoder::Start() {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (status_ != DecoderStatus::INITIALIZED) {
        LOGE("Decoder not initialized");
        return false;
    }
    
    // 启动 MediaCodec
    media_status_t status = AMediaCodec_start(mediaCodec_);
    if (status != AMEDIA_OK) {
        LOGE("AMediaCodec_start failed: %s", 
             MediaCodecUtils::ErrorToString(status).c_str());
        status_ = DecoderStatus::ERROR;
        return false;
    }
    
    status_ = DecoderStatus::RUNNING;
    running_ = true;
    
    // 启动输出处理线程
    outputThread_ = std::thread(&MediaCodecDecoder::OutputThread, this);
    
    LOGI("Decoder started");
    return true;
}

bool MediaCodecDecoder::Stop() {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (status_ != DecoderStatus::RUNNING) {
        return false;
    }
    
    running_ = false;
    
    // 停止解码器
    if (mediaCodec_) {
        AMediaCodec_stop(mediaCodec_);
    }
    
    // 等待输出线程结束
    if (outputThread_.joinable()) {
        outputThread_.join();
    }
    
    status_ = DecoderStatus::STOPPED;
    LOGI("Decoder stopped");
    return true;
}

bool MediaCodecDecoder::Restart() {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (status_ != DecoderStatus::STOPPED && 
        status_ != DecoderStatus::ERROR) {
        return false;
    }
    
    // 清理并重新初始化
    CleanupInternal();
    if (!InitializeInternal()) {
        status_ = DecoderStatus::ERROR;
        return false;
    }
    
    status_ = DecoderStatus::INITIALIZED;
    
    // 重新启动
    return Start();
}

bool MediaCodecDecoder::DecodeFrame(const uint8_t* data, size_t size,
                                   int64_t presentationTimeUs,
                                   int flags) {
    if (status_ != DecoderStatus::RUNNING || !mediaCodec_) {
        return false;
    }
    
    auto startTime = std::chrono::steady_clock::now();
    
    // 获取输入缓冲区
    ssize_t inputBufferIndex = AMediaCodec_dequeueInputBuffer(mediaCodec_, 10000);
    if (inputBufferIndex < 0) {
        if (inputBufferIndex == AMEDIACODEC_INFO_TRY_AGAIN_LATER) {
            // LOGD("No input buffer available");
        } else {
            LOGE("Failed to dequeue input buffer: %zd", inputBufferIndex);
        }
        return false;
    }
    
    // 获取输入缓冲区
    size_t bufferSize = 0;
    uint8_t* inputBuffer = AMediaCodec_getInputBuffer(mediaCodec_, 
                                                      inputBufferIndex, 
                                                      &bufferSize);
    if (!inputBuffer) {
        LOGE("Failed to get input buffer");
        return false;
    }
    
    if (size > bufferSize) {
        LOGE("Input data too large: %zu > %zu", size, bufferSize);
        return false;
    }
    
    // 复制数据
    memcpy(inputBuffer, data, size);
    
    // 计算时间戳（如果没有提供）
    if (presentationTimeUs == 0) {
        presentationTimeUs = frameCount_ * 1000000LL / config_.frameRate;
        frameCount_++;
    }
    
    // 提交输入缓冲区
    media_status_t status = AMediaCodec_queueInputBuffer(mediaCodec_,
                                                        inputBufferIndex,
                                                        0, // offset
                                                        size,
                                                        presentationTimeUs,
                                                        flags);
    
    auto endTime = std::chrono::steady_clock::now();
    auto decodeTime = std::chrono::duration_cast<std::chrono::microseconds>(
        endTime - startTime).count();
    
    if (status != AMEDIA_OK) {
        LOGE("Failed to queue input buffer: %s", 
             MediaCodecUtils::ErrorToString(status).c_str());
        UpdateStats(false, size, decodeTime);
        return false;
    }
    
    lastPresentationTimeUs_ = presentationTimeUs;
    UpdateStats(true, size, decodeTime);
    
    return true;
}

void MediaCodecDecoder::OutputThread() {
    LOGD("Output thread started");
    
    while (running_) {
        if (!ProcessOutput(10000)) {
            // 短暂休眠避免忙等待
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }
    
    LOGD("Output thread stopped");
}

bool MediaCodecDecoder::ProcessOutput(int timeoutUs) {
    if (!mediaCodec_ || !running_) {
        return false;
    }
    
    AMediaCodecBufferInfo bufferInfo;
    ssize_t outputBufferIndex = AMediaCodec_dequeueOutputBuffer(mediaCodec_, 
                                                               &bufferInfo, 
                                                               timeoutUs);
    
    if (outputBufferIndex >= 0) {
        // 处理输出缓冲区
        bool processed = ProcessOutputBuffer(outputBufferIndex, bufferInfo);
        
        // 释放输出缓冲区
        AMediaCodec_releaseOutputBuffer(mediaCodec_, outputBufferIndex, 
                                       config_.useSurface);
        
        // 检查是否是结束流
        if ((bufferInfo.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != 0) {
            LOGI("End of stream received");
            running_ = false;
        }
        
        return processed;
    } 
    else if (outputBufferIndex == AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED) {
        LOGD("Output buffers changed");
        return true;
    } 
    else if (outputBufferIndex == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED) {
        // 获取新的输出格式
        AMediaFormat* format = AMediaCodec_getOutputFormat(mediaCodec_);
        if (format) {
            HandleOutputFormatChanged(format);
            AMediaFormat_delete(format);
        }
        return true;
    } 
    else if (outputBufferIndex == AMEDIACODEC_INFO_TRY_AGAIN_LATER) {
        // 没有可用的输出缓冲区
        return false;
    } 
    else {
        LOGE("Unexpected error dequeueing output buffer: %zd", outputBufferIndex);
        return false;
    }
}

bool MediaCodecDecoder::ProcessOutputBuffer(size_t bufferIndex,
                                           const AMediaCodecBufferInfo& info) {
    if (info.size <= 0) {
        return false;
    }
    
    if (config_.useSurface) {
        // Surface 模式：直接渲染
        if (frameCallback_) {
            DecodedFrame frame;
            frame.width = outputWidth_ > 0 ? outputWidth_ : config_.width;
            frame.height = outputHeight_ > 0 ? outputHeight_ : config_.height;
            frame.presentationTimeUs = info.presentationTimeUs;
            frame.flags = info.flags;
            frame.isKeyFrame = (info.flags & AMEDIACODEC_BUFFER_FLAG_KEY_FRAME) != 0;
            frameCallback_->OnFrameDecoded(frame);
        }
        return true;
    } else {
        // 缓冲区模式：提取 YUV 数据
        DecodedFrame frame;
        if (ExtractFrameFromBuffer(bufferIndex, info, frame)) {
            // 存储解码帧
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (decodedFrames_.size() >= maxFrames_) {
                    decodedFrames_.pop();
                    stats_.framesDropped++;
                }
                decodedFrames_.push(frame);
            }
            condition_.notify_one();
            
            // 调用回调
            if (frameCallback_) {
                frameCallback_->OnFrameDecoded(frame);
            }
            
            stats_.framesDecoded++;
            return true;
        }
    }
    
    return false;
}

bool MediaCodecDecoder::ExtractFrameFromBuffer(size_t bufferIndex,
                                              const AMediaCodecBufferInfo& info,
                                              DecodedFrame& frame) {
    return ExtractYUVData(bufferIndex, info, frame);
}

bool MediaCodecDecoder::ExtractYUVData(size_t bufferIndex,
                                      const AMediaCodecBufferInfo& info,
                                      DecodedFrame& frame) {
    // 获取输出缓冲区
    size_t bufferSize = 0;
    uint8_t* buffer = AMediaCodec_getOutputBuffer(mediaCodec_, bufferIndex, &bufferSize);
    if (!buffer || bufferSize == 0) {
        return false;
    }
    
    // 设置帧信息
    frame.width = outputWidth_ > 0 ? outputWidth_ : config_.width;
    frame.height = outputHeight_ > 0 ? outputHeight_ : config_.height;
    frame.presentationTimeUs = info.presentationTimeUs;
    frame.flags = info.flags;
    frame.isKeyFrame = (info.flags & AMEDIACODEC_BUFFER_FLAG_KEY_FRAME) != 0;
    
    // 根据颜色格式提取 YUV 数据
    int actualColorFormat = outputColorFormat_ > 0 ? outputColorFormat_ : 
                           static_cast<int>(config_.colorFormat);
    
    switch (actualColorFormat) {
        case static_cast<int>(ColorFormat::YUV420_PLANAR): // YV12/YU12
        {
            int ySize = frame.width * frame.height;
            int uvSize = ySize / 4;
            
            frame.yData.resize(ySize);
            frame.uData.resize(uvSize);
            frame.vData.resize(uvSize);
            
            memcpy(frame.yData.data(), buffer, ySize);
            
            // 检查是 YV12 还是 YU12
            uint8_t* uvStart = buffer + ySize;
            if (frame.height % 2 == 0) {
                // YV12: Y, V, U
                memcpy(frame.vData.data(), uvStart, uvSize);
                memcpy(frame.uData.data(), uvStart + uvSize, uvSize);
            } else {
                // YU12: Y, U, V
                memcpy(frame.uData.data(), uvStart, uvSize);
                memcpy(frame.vData.data(), uvStart + uvSize, uvSize);
            }
            break;
        }
        
        case static_cast<int>(ColorFormat::YUV420_SEMIPLANAR): // NV12
        {
            int ySize = frame.width * frame.height;
            int uvSize = ySize / 2;
            
            frame.yData.resize(ySize);
            frame.uData.resize(uvSize / 2);
            frame.vData.resize(uvSize / 2);
            
            memcpy(frame.yData.data(), buffer, ySize);
            
            // 分离 UV 交错数据
            uint8_t* uvStart = buffer + ySize;
            for (int i = 0; i < uvSize; i += 2) {
                frame.uData[i / 2] = uvStart[i];     // U
                frame.vData[i / 2] = uvStart[i + 1]; // V
            }
            break;
        }
        
        case static_cast<int>(ColorFormat::YUV420_PACKED_SEMIPLANAR): // NV21
        {
            int ySize = frame.width * frame.height;
            int uvSize = ySize / 2;
            
            frame.yData.resize(ySize);
            frame.vData.resize(uvSize / 2);
            frame.uData.resize(uvSize / 2);
            
            memcpy(frame.yData.data(), buffer, ySize);
            
            // 分离 VU 交错数据
            uint8_t* vuStart = buffer + ySize;
            for (int i = 0; i < uvSize; i += 2) {
                frame.vData[i / 2] = vuStart[i];     // V
                frame.uData[i / 2] = vuStart[i + 1]; // U
            }
            break;
        }
        
        default:
            LOGE("Unsupported color format: %d", actualColorFormat);
            return false;
    }
    
    return true;
}

void MediaCodecDecoder::HandleOutputFormatChanged(AMediaFormat* format) {
    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_WIDTH, &outputWidth_);
    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_HEIGHT, &outputHeight_);
    AMediaFormat_getInt32(format, AMEDIAFORMAT_KEY_COLOR_FORMAT, &outputColorFormat_);
    
    LOGI("Output format changed: %dx%d, color format: %d", 
         outputWidth_, outputHeight_, outputColorFormat_);
    
    if (frameCallback_) {
        frameCallback_->OnFormatChanged(outputWidth_, outputHeight_, outputColorFormat_);
    }
}

bool MediaCodecDecoder::GetDecodedFrame(DecodedFrame& frame, int timeoutUs) {
    std::unique_lock<std::mutex> lock(mutex_);
    
    if (decodedFrames_.empty()) {
        if (timeoutUs > 0) {
            auto timeout = std::chrono::microseconds(timeoutUs);
            if (!condition_.wait_for(lock, timeout, 
                [this]() { return !decodedFrames_.empty() || !running_; })) {
                return false; // 超时
            }
        } else {
            condition_.wait(lock, 
                [this]() { return !decodedFrames_.empty() || !running_; });
        }
        
        if (!running_ || decodedFrames_.empty()) {
            return false;
        }
    }
    
    frame = decodedFrames_.front();
    decodedFrames_.pop();
    
    return true;
}

bool MediaCodecDecoder::GetYUVData(std::vector<uint8_t>& yuvData,
                                  int& width, int& height,
                                  int timeoutUs) {
    DecodedFrame frame;
    if (!GetDecodedFrame(frame, timeoutUs)) {
        return false;
    }
    
    // 合并 YUV 数据
    size_t totalSize = frame.yData.size() + frame.uData.size() + frame.vData.size();
    yuvData.resize(totalSize);
    
    size_t offset = 0;
    memcpy(yuvData.data() + offset, frame.yData.data(), frame.yData.size());
    offset += frame.yData.size();
    memcpy(yuvData.data() + offset, frame.uData.data(), frame.uData.size());
    offset += frame.uData.size();
    memcpy(yuvData.data() + offset, frame.vData.data(), frame.vData.size());
    
    width = frame.width;
    height = frame.height;
    
    return true;
}

bool MediaCodecDecoder::Flush() {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (status_ != DecoderStatus::RUNNING || !mediaCodec_) {
        return false;
    }
    
    media_status_t status = AMediaCodec_flush(mediaCodec_);
    if (status != AMEDIA_OK) {
        LOGE("AMediaCodec_flush failed: %s", 
             MediaCodecUtils::ErrorToString(status).c_str());
        return false;
    }
    
    // 清空帧队列
    std::queue<DecodedFrame> emptyQueue;
    std::swap(decodedFrames_, emptyQueue);
    
    LOGI("Decoder flushed");
    return true;
}

void MediaCodecDecoder::Release() {
    Stop();
    CleanupInternal();
    status_ = DecoderStatus::UNINITIALIZED;
    LOGI("Decoder released");
}

void MediaCodecDecoder::CleanupInternal() {
    // 清理 MediaCodec
    if (mediaCodec_) {
        AMediaCodec_delete(mediaCodec_);
        mediaCodec_ = nullptr;
    }
    
    // 清理 MediaFormat
    if (mediaFormat_) {
        AMediaFormat_delete(mediaFormat_);
        mediaFormat_ = nullptr;
    }
    
    // 清理 Surface
    if (outputSurface_ && config_.surface == outputSurface_) {
        ANativeWindow_release(outputSurface_);
        outputSurface_ = nullptr;
    }
    
    // 清空帧队列
    std::queue<DecodedFrame> emptyQueue;
    std::swap(decodedFrames_, emptyQueue);
    
    // 重置状态
    outputWidth_ = 0;
    outputHeight_ = 0;
    outputColorFormat_ = 0;
    lastPresentationTimeUs_ = 0;
    frameCount_ = 0;
    
    RemoveFrameCallback();
}

void MediaCodecDecoder::SetFrameCallback(FrameCallback* callback) {
    std::lock_guard<std::mutex> lock(mutex_);
    frameCallback_ = callback;
}

void MediaCodecDecoder::RemoveFrameCallback() {
    std::lock_guard<std::mutex> lock(mutex_);
    frameCallback_ = nullptr;
}

bool MediaCodecDecoder::UpdateConfig(const DecoderConfig& config) {
    std::lock_guard<std::mutex> lock(mutex_);
    
    if (status_ == DecoderStatus::RUNNING) {
        LOGE("Cannot update config while decoder is running");
        return false;
    }
    
    config_ = config;
    
    // 如果已经初始化，需要重新配置
    if (status_ == DecoderStatus::INITIALIZED) {
        CleanupInternal();
        if (!InitializeInternal()) {
            status_ = DecoderStatus::ERROR;
            return false;
        }
    }
    
    return true;
}

void MediaCodecDecoder::ResetStats() {
    std::lock_guard<std::mutex> lock(mutex_);
    stats_ = DecoderStats();
    lastStatTime_ = std::chrono::steady_clock::now();
}

void MediaCodecDecoder::UpdateStats(bool success, size_t bytes, int64_t decodeTime) {
    std::lock_guard<std::mutex> lock(mutex_);
    
    auto now = std::chrono::steady_clock::now();
    auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        now - lastStatTime_).count();
    
    if (elapsed >= 1000) { // 每秒更新一次平均值
        if (stats_.framesDecoded > 0) {
            stats_.averageDecodeTimeMs = 
                stats_.averageDecodeTimeMs * 0.9 + (decodeTime / 1000.0) * 0.1;
        }
        lastStatTime_ = now;
    }
    
    stats_.bytesProcessed += bytes;
    stats_.lastFrameTimestamp = decodeTime;
}

} // namespace RyujinxMediaCodec
