// oboe_audio_renderer.cpp (修复版)
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <mutex>  

// 声明 logToFile 函数
extern "C" void logToFile(int level, const char* tag, const char* format, ...);

// =============== RingBuffer Implementation ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity), mReadIndex(0), mWriteIndex(0)
{
    logToFile(3, "OboeAudio", "RingBuffer created with capacity: %zu", capacity);
    logToFile(3, "OboeAudio", "环形缓冲区已创建，容量: %zu", capacity);
}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0) return true;

    std::lock_guard<std::mutex> lock(mMutex);
    size_t writeIndex = mWriteIndex.load();
    size_t readIndex = mReadIndex.load();

    size_t available = (readIndex > writeIndex) ? (readIndex - writeIndex - 1) : 
                     (mCapacity - writeIndex + readIndex - 1);
    
    if (count > available) {
        logToFile(5, "OboeAudio", "RingBuffer write overflow: requested %zu, available %zu", count, available);
        logToFile(5, "OboeAudio", "环形缓冲区写入溢出: 请求 %zu, 可用 %zu", count, available);
        return false;
    }

    size_t end = writeIndex + count;
    if (end <= mCapacity) {
        std::memcpy(&mBuffer[writeIndex], data, count * sizeof(float));
    } else {
        size_t part1 = mCapacity - writeIndex;
        std::memcpy(&mBuffer[writeIndex], data, part1 * sizeof(float));
        std::memcpy(&mBuffer[0], data + part1, (count - part1) * sizeof(float));
    }

    mWriteIndex.store((writeIndex + count) % mCapacity);
    logToFile(3, "OboeAudio", "RingBuffer wrote %zu samples, new write index: %zu", count, mWriteIndex.load());
    logToFile(3, "OboeAudio", "环形缓冲区写入 %zu 样本, 新写入位置: %zu", count, mWriteIndex.load());
    return true;
}

size_t RingBuffer::read(float* output, size_t count) {
    if (count == 0) return 0;

    std::lock_guard<std::mutex> lock(mMutex);
    size_t writeIndex = mWriteIndex.load();
    size_t readIndex = mReadIndex.load();

    size_t availableSamples = (writeIndex >= readIndex) ? (writeIndex - readIndex) : 
                             (mCapacity - readIndex + writeIndex);
    
    size_t toRead = std::min(count, availableSamples);
    if (toRead == 0) {
        logToFile(5, "OboeAudio", "RingBuffer read underflow: requested %zu, available %zu", count, availableSamples);
        logToFile(5, "OboeAudio", "环形缓冲区读取不足: 请求 %zu, 可用 %zu", count, availableSamples);
        return 0;
    }

    size_t end = readIndex + toRead;
    if (end <= mCapacity) {
        std::memcpy(output, &mBuffer[readIndex], toRead * sizeof(float));
    } else {
        size_t part1 = mCapacity - readIndex;
        std::memcpy(output, &mBuffer[readIndex], part1 * sizeof(float));
        std::memcpy(output + part1, &mBuffer[0], (toRead - part1) * sizeof(float));
    }

    mReadIndex.store((readIndex + toRead) % mCapacity);
    logToFile(3, "OboeAudio", "RingBuffer read %zu samples, new read index: %zu", toRead, mReadIndex.load());
    logToFile(3, "OboeAudio", "环形缓冲区读取 %zu 样本, 新读取位置: %zu", toRead, mReadIndex.load());
    return toRead;
}

size_t RingBuffer::available() const {
    std::lock_guard<std::mutex> lock(mMutex);
    size_t write = mWriteIndex.load();
    size_t read = mReadIndex.load();
    
    if (write >= read) {
        return write - read;
    } else {
        return mCapacity - read + write;
    }
}

size_t RingBuffer::availableForWrite() const {
    std::lock_guard<std::mutex> lock(mMutex);
    size_t write = mWriteIndex.load();
    size_t read = mReadIndex.load();
    
    if (write >= read) {
        return mCapacity - write + read - 1;
    } else {
        return read - write - 1;
    }
}

void RingBuffer::clear() {
    std::lock_guard<std::mutex> lock(mMutex);
    mReadIndex.store(0);
    mWriteIndex.store(0);
    logToFile(3, "OboeAudio", "RingBuffer cleared");
    logToFile(3, "OboeAudio", "环形缓冲区已清空");
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>(48000 * 2 * 5)) // 减少到5秒缓冲以减少延迟
{
    logToFile(3, "OboeAudio", "OboeAudioRenderer constructor called");
    logToFile(3, "OboeAudio", "Oboe音频渲染器构造函数调用");
}

OboeAudioRenderer::~OboeAudioRenderer() {
    logToFile(3, "OboeAudio", "OboeAudioRenderer destructor called");
    logToFile(3, "OboeAudio", "Oboe音频渲染器析构函数调用");
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::openStreamWithFormat(oboe::AudioFormat format) {
    oboe::AudioStreamBuilder builder;

    logToFile(3, "OboeAudio", "Attempting to open stream with format: %d", format);
    logToFile(3, "OboeAudio", "尝试打开格式为 %d 的音频流", format);
    
    // 尝试不同的音频API
    const int maxApiRetries = 3;
    oboe::AudioApi audioApis[] = {
        oboe::AudioApi::AAudio,
        oboe::AudioApi::OpenSLES,
        oboe::AudioApi::Unspecified
    };
    
    oboe::Result result = oboe::Result::ErrorInternal;
    
    for (int apiAttempt = 0; apiAttempt < maxApiRetries; apiAttempt++) {
        builder.setAudioApi(audioApis[apiAttempt]);
        
        builder.setDirection(oboe::Direction::Output)
               ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
               ->setSharingMode(oboe::SharingMode::Shared)
               ->setFormat(format)
               ->setChannelCount(mChannelCount.load())
               ->setSampleRate(mSampleRate.load())
               ->setBufferCapacityInFrames(mBufferSize.load())
               ->setDataCallback(this)
               ->setErrorCallback(this);

        result = builder.openStream(mAudioStream);
        
        if (result == oboe::Result::OK) {
            const char* apiName = "Unknown";
            switch (audioApis[apiAttempt]) {
                case oboe::AudioApi::AAudio: apiName = "AAudio"; break;
                case oboe::AudioApi::OpenSLES: apiName = "OpenSL ES"; break;
                default: apiName = "Unspecified"; break;
            }
            
            logToFile(4, "OboeAudio", "Successfully opened audio stream with format %d using %s", 
                     format, apiName);
            logToFile(4, "OboeAudio", "成功打开音频流，格式 %d，使用 %s", 
                     format, apiName);
            break;
        } else {
            logToFile(5, "OboeAudio", "Failed to open stream with format %d using API %d: %s", 
                     format, audioApis[apiAttempt], oboe::convertToText(result));
            logToFile(5, "OboeAudio", "使用API %d 打开格式 %d 的流失败: %s", 
                     audioApis[apiAttempt], format, oboe::convertToText(result));
        }
    }

    if (result != oboe::Result::OK) {
        logToFile(6, "OboeAudio", "All API attempts failed for format %d: %s", format, oboe::convertToText(result));
        logToFile(6, "OboeAudio", "所有API尝试都失败，格式 %d: %s", format, oboe::convertToText(result));
        return false;
    }

    return true;
}

void OboeAudioRenderer::updateStreamParameters() {
    if (mAudioStream) {
        mSampleRate.store(mAudioStream->getSampleRate());
        mBufferSize.store(mAudioStream->getBufferSizeInFrames());
        mChannelCount.store(mAudioStream->getChannelCount());
        mAudioFormat.store(mAudioStream->getFormat());
        
        // 获取实际使用的音频API
        const char* apiName = "Unknown";
        switch (mAudioStream->getAudioApi()) {
            case oboe::AudioApi::AAudio: apiName = "AAudio"; break;
            case oboe::AudioApi::OpenSLES: apiName = "OpenSL ES"; break;
            default: apiName = "Unspecified"; break;
        }
        
        logToFile(4, "OboeAudio", "Stream parameters updated: SampleRate=%d, BufferSize=%d, Channels=%d, Format=%d, API=%s",
              mSampleRate.load(), mBufferSize.load(), mChannelCount.load(), static_cast<int>(mAudioFormat.load()), apiName);
        logToFile(4, "OboeAudio", "流参数更新: 采样率=%d, 缓冲区大小=%d, 声道数=%d, 格式=%d, API=%s",
              mSampleRate.load(), mBufferSize.load(), mChannelCount.load(), static_cast<int>(mAudioFormat.load()), apiName);
    }
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized.load()) {
        logToFile(3, "OboeAudio", "OboeAudioRenderer already initialized");
        logToFile(3, "OboeAudio", "Oboe音频渲染器已初始化");
        return true;
    }

    std::lock_guard<std::mutex> lock(mInitMutex);
    if (mIsInitialized.load()) {
        logToFile(3, "OboeAudio", "OboeAudioRenderer already initialized (double-checked)");
        logToFile(3, "OboeAudio", "Oboe音频渲染器已初始化(双重检查)");
        return true;
    }

    logToFile(4, "OboeAudio", "Initializing OboeAudioRenderer (stream not started yet)...");
    logToFile(4, "OboeAudio", "初始化Oboe音频渲染器(流尚未启动)...");
    
    const int maxRetries = 3;
    for (int attempt = 0; attempt < maxRetries; attempt++) {
        if (attempt > 0) {
            logToFile(4, "OboeAudio", "Retry attempt %d/%d", attempt + 1, maxRetries);
            logToFile(4, "OboeAudio", "重试尝试 %d/%d", attempt + 1, maxRetries);
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        
        // 先尝试I16格式，因为它更可能与C#端匹配
        if (!openStreamWithFormat(oboe::AudioFormat::I16)) {
            logToFile(5, "OboeAudio", "I16 format failed, trying Float format");
            logToFile(5, "OboeAudio", "I16格式失败，尝试Float格式");
            if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
                logToFile(6, "OboeAudio", "All audio format attempts failed");
                logToFile(6, "OboeAudio", "所有音频格式尝试都失败");
                continue;
            }
        }

        updateStreamParameters();
        mIsInitialized.store(true);
        logToFile(4, "OboeAudio", "Oboe stream opened (not started) on attempt %d", attempt + 1);
        logToFile(4, "OboeAudio", "Oboe流已打开(未启动)，尝试次数 %d", attempt + 1);
        return true;
    }
    
    logToFile(6, "OboeAudio", "All initialization attempts failed");
    logToFile(6, "OboeAudio", "所有初始化尝试都失败");
    return false;
}

void OboeAudioRenderer::shutdown() {
    logToFile(4, "OboeAudio", "Shutting down OboeAudioRenderer");
    logToFile(4, "OboeAudio", "关闭Oboe音频渲染器");
    if (mAudioStream) {
        if (mIsStreamStarted.load()) {
            logToFile(3, "OboeAudio", "Stopping audio stream");
            logToFile(3, "OboeAudio", "停止音频流");
            mAudioStream->stop();
        }
        logToFile(3, "OboeAudio", "Closing audio stream");
        logToFile(3, "OboeAudio", "关闭音频流");
        mAudioStream->close();
        mAudioStream.reset();
        logToFile(3, "OboeAudio", "Audio stream released");
        logToFile(3, "OboeAudio", "音频流已释放");
    }
    if (mRingBuffer) {
        logToFile(3, "OboeAudio", "Clearing ring buffer");
        logToFile(3, "OboeAudio", "清空环形缓冲区");
        mRingBuffer->clear();
    }
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    logToFile(4, "OboeAudio", "OboeAudioRenderer shutdown complete");
    logToFile(4, "OboeAudio", "Oboe音频渲染器关闭完成");
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        logToFile(5, "OboeAudio", "Invalid sample rate: %d", sampleRate);
        logToFile(5, "OboeAudio", "无效采样率: %d", sampleRate);
        return;
    }
    logToFile(3, "OboeAudio", "Setting sample rate to: %d", sampleRate);
    logToFile(3, "OboeAudio", "设置采样率为: %d", sampleRate);
    mSampleRate.store(sampleRate);
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) {
        logToFile(5, "OboeAudio", "Invalid buffer size: %d", bufferSize);
        logToFile(5, "OboeAudio", "无效缓冲区大小: %d", bufferSize);
        return;
    }
    logToFile(3, "OboeAudio", "Setting buffer size to: %d", bufferSize);
    logToFile(3, "OboeAudio", "设置缓冲区大小为: %d", bufferSize);
    mBufferSize.store(bufferSize);
}

void OboeAudioRenderer::setVolume(float volume) {
    float clampedVolume = std::clamp(volume, 0.0f, 1.0f);
    logToFile(3, "OboeAudio", "Setting volume to: %.2f", clampedVolume);
    logToFile(3, "OboeAudio", "设置音量为: %.2f", clampedVolume);
    mVolume.store(clampedVolume);
}

void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames) {
    // 检查初始化状态，如果未初始化则尝试初始化
    if (!mIsInitialized.load()) {
        logToFile(4, "OboeAudio", "Renderer not initialized, attempting to initialize...");
        logToFile(4, "OboeAudio", "渲染器未初始化，尝试初始化...");
        if (!initialize()) {
            logToFile(5, "OboeAudio", "Failed to initialize in writeAudio");
            logToFile(5, "OboeAudio", "在writeAudio中初始化失败");
            return;
        }
    }

    if (!data || numFrames <= 0) {
        logToFile(5, "OboeAudio", "Invalid audio data or frame count: %d", numFrames);
        logToFile(5, "OboeAudio", "无效音频数据或帧数: %d", numFrames);
        return;
    }

    // 记录前几个样本的值用于调试
    if (numFrames > 0) {
        logToFile(3, "OboeAudio", "First few samples: [0]=%.6f, [1]=%.6f, [2]=%.6f, [3]=%.6f", 
                 data[0], data[1], data[2], data[3]);
        logToFile(3, "OboeAudio", "前几个样本: [0]=%.6f, [1]=%.6f, [2]=%.6f, [3]=%.6f", 
                 data[0], data[1], data[2], data[3]);
    }

    // 首次写入时启动流
    if (!mIsStreamStarted.load()) {
        std::lock_guard<std::mutex> lock(mInitMutex);
        if (!mIsStreamStarted.load()) {
            oboe::Result result = mAudioStream->requestStart();
            if (result == oboe::Result::OK) {
                mIsStreamStarted.store(true);
                logToFile(4, "OboeAudio", "Audio stream started on first write!");
                logToFile(4, "OboeAudio", "首次写入时启动音频流!");
                
                // 预填充 50ms 静音，减少延迟
                int32_t frames = mSampleRate.load() / 20; // 50ms
                int32_t channels = mChannelCount.load();
                std::vector<float> silence(frames * channels, 0.0f);
                mRingBuffer->write(silence.data(), silence.size());
                logToFile(4, "OboeAudio", "Pre-filled %d frames of silence", frames);
                logToFile(4, "OboeAudio", "预填充 %d 帧静音", frames);
            } else {
                logToFile(6, "OboeAudio", "Failed to start audio stream: %s", oboe::convertToText(result));
                logToFile(6, "OboeAudio", "启动音频流失败: %s", oboe::convertToText(result));
                return;
            }
        }
    }

    int32_t channelCount = mChannelCount.load();
    size_t totalSamples = numFrames * channelCount;
    
    logToFile(3, "OboeAudio", "Writing %d frames (%zu samples) to ring buffer", numFrames, totalSamples);
    logToFile(3, "OboeAudio", "写入 %d 帧 (%zu 样本) 到环形缓冲区", numFrames, totalSamples);
    
    if (!mRingBuffer->write(data, totalSamples)) {
        logToFile(5, "OboeAudio", "Ring buffer overflow when writing %zu samples", totalSamples);
        logToFile(5, "OboeAudio", "环形缓冲区溢出，当写入 %zu 样本时", totalSamples);
    } else {
        logToFile(3, "OboeAudio", "Successfully wrote %zu samples to ring buffer", totalSamples);
        logToFile(3, "OboeAudio", "成功写入 %zu 样本到环形缓冲区", totalSamples);
    }
}

void OboeAudioRenderer::clearBuffer() {
    logToFile(3, "OboeAudio", "Clearing audio buffer");
    logToFile(3, "OboeAudio", "清空音频缓冲区");
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
}

size_t OboeAudioRenderer::getBufferedFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    size_t frames = mRingBuffer->available() / channelCount;
    logToFile(3, "OboeAudio", "Buffered frames: %zu", frames);
    logToFile(3, "OboeAudio", "已缓冲帧数: %zu", frames);
    return frames;
}

size_t OboeAudioRenderer::getAvailableFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    size_t frames = mRingBuffer->availableForWrite() / channelCount;
    logToFile(3, "OboeAudio", "Available frames: %zu", frames);
    logToFile(3, "OboeAudio", "可用帧数: %zu", frames);
    return frames;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    // 获取当前参数值
    int32_t channelCount = mChannelCount.load();
    oboe::AudioFormat audioFormat = mAudioFormat.load();
    float volume = mVolume.load();

    // 记录回调信息
    logToFile(3, "OboeAudio", "Audio callback: %d frames, %d channels, format %d, volume %.2f", 
          numFrames, channelCount, static_cast<int>(audioFormat), volume);
    logToFile(3, "OboeAudio", "音频回调: %d 帧, %d 声道, 格式 %d, 音量 %.2f", 
          numFrames, channelCount, static_cast<int>(audioFormat), volume);

    // 流未启动 → 静音，无警告
    if (!mIsStreamStarted.load()) {
        if (audioFormat == oboe::AudioFormat::I16) {
            memset(audioData, 0, numFrames * channelCount * sizeof(int16_t));
        } else {
            memset(audioData, 0, numFrames * channelCount * sizeof(float));
        }
        logToFile(3, "OboeAudio", "Stream not started, outputting silence");
        logToFile(3, "OboeAudio", "流未启动，输出静音");
        return oboe::DataCallbackResult::Continue;
    }

    size_t totalSamples = numFrames * channelCount;
    
    if (audioFormat == oboe::AudioFormat::I16) {
        int16_t* output = static_cast<int16_t*>(audioData);
        std::vector<float> floatData(totalSamples);
        
        size_t read = mRingBuffer->read(floatData.data(), totalSamples);
        
        if (read < totalSamples) {
            logToFile(5, "OboeAudio", "Audio underflow: requested %zu samples, got %zu", totalSamples, read);
            logToFile(5, "OboeAudio", "音频欠载: 请求 %zu 样本, 得到 %zu", totalSamples, read);
            std::fill(floatData.begin() + read, floatData.end(), 0.0f);
        }
        
        for (size_t i = 0; i < totalSamples; i++) {
            float sample = floatData[i] * volume;
            sample = std::clamp(sample, -1.0f, 1.0f);
            output[i] = static_cast<int16_t>(sample * 32767);
        }
        
        // 记录前几个输出样本用于调试
        if (totalSamples >= 4) {
            logToFile(3, "OboeAudio", "I16 output samples: [0]=%d, [1]=%d, [2]=%d, [3]=%d", 
                     output[0], output[1], output[2], output[3]);
            logToFile(3, "OboeAudio", "I16输出样本: [0]=%d, [1]=%d, [2]=%d, [3]=%d", 
                     output[0], output[1], output[2], output[3]);
        }
        
        logToFile(3, "OboeAudio", "Processed %zu samples for I16 output", totalSamples);
        logToFile(3, "OboeAudio", "处理 %zu 样本用于I16输出", totalSamples);
    } else {
        float* output = static_cast<float*>(audioData);
        size_t read = mRingBuffer->read(output, totalSamples);
        
        if (read < totalSamples) {
            logToFile(5, "OboeAudio", "Audio underflow: requested %zu samples, got %zu", totalSamples, read);
            logToFile(5, "OboeAudio", "音频欠载: 请求 %zu 样本, 得到 %zu", totalSamples, read);
            std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
        }
        
        if (volume != 1.0f) {
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
            }
        }
        
        // 记录前几个输出样本用于调试
        if (totalSamples >= 4) {
            logToFile(3, "OboeAudio", "Float output samples: [0]=%.6f, [1]=%.6f, [2]=%.6f, [3]=%.6f", 
                     output[0], output[1], output[2], output[3]);
            logToFile(3, "OboeAudio", "Float输出样本: [0]=%.6f, [1]=%.6f, [2]=%.6f, [3]=%.6f", 
                     output[0], output[1], output[2], output[3]);
        }
        
        logToFile(3, "OboeAudio", "Processed %zu samples for Float output", totalSamples);
        logToFile(3, "OboeAudio", "处理 %zu 样本用于Float输出", totalSamples);
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    logToFile(6, "OboeAudio", "Audio error after close: %s", oboe::convertToText(error));
    logToFile(6, "OboeAudio", "关闭后音频错误: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    logToFile(6, "OboeAudio", "Audio error before close: %s", oboe::convertToText(error));
    logToFile(6, "OboeAudio", "关闭前音频错误: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
}
