// oboe_audio_renderer.cpp (极致优化版)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <mutex>  

// =============== RingBuffer Implementation ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity), mReadIndex(0), mWriteIndex(0)
{
}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0) return true;

    std::lock_guard<std::mutex> lock(mMutex);
    size_t writeIndex = mWriteIndex.load();
    size_t readIndex = mReadIndex.load();

    size_t available = (readIndex > writeIndex) ? (readIndex - writeIndex - 1) : 
                     (mCapacity - writeIndex + readIndex - 1);
    
    if (count > available) {
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
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>((48000 * 2 * 100) / 1000)) // 100ms缓冲
{
}

OboeAudioRenderer::~OboeAudioRenderer() {
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::openStreamWithFormat(oboe::AudioFormat format) {
    oboe::AudioStreamBuilder builder;
    
    // 尝试不同的音频API
    const int maxApiRetries = 2; // 减少重试次数
    oboe::AudioApi audioApis[] = {
        oboe::AudioApi::AAudio,
        oboe::AudioApi::OpenSLES
    };
    
    oboe::Result result = oboe::Result::ErrorInternal;
    
    for (int apiAttempt = 0; apiAttempt < maxApiRetries; apiAttempt++) {
        builder.setAudioApi(audioApis[apiAttempt]);
        
        builder.setDirection(oboe::Direction::Output)
               ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
               ->setSharingMode(oboe::SharingMode::Exclusive) // 使用独占模式减少延迟
               ->setFormat(format)
               ->setChannelCount(mChannelCount.load())
               ->setSampleRate(mSampleRate.load())
               ->setBufferCapacityInFrames(oboe::DefaultStreamValues::FramesPerBurst * 2) // 最小缓冲区
               ->setDataCallback(this)
               ->setErrorCallback(this);

        result = builder.openStream(mAudioStream);
        
        if (result == oboe::Result::OK) {
            break;
        }
    }

    return result == oboe::Result::OK;
}

void OboeAudioRenderer::updateStreamParameters() {
    if (mAudioStream) {
        mSampleRate.store(mAudioStream->getSampleRate());
        mBufferSize.store(mAudioStream->getBufferSizeInFrames());
        mChannelCount.store(mAudioStream->getChannelCount());
        mAudioFormat.store(mAudioStream->getFormat());
    }
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized.load()) {
        return true;
    }

    std::lock_guard<std::mutex> lock(mInitMutex);
    if (mIsInitialized.load()) {
        return true;
    }
    
    const int maxRetries = 2; // 减少重试次数
    for (int attempt = 0; attempt < maxRetries; attempt++) {
        if (attempt > 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(50)); // 减少等待时间
        }
        
        // 先尝试I16格式
        if (!openStreamWithFormat(oboe::AudioFormat::I16)) {
            if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
                continue;
            }
        }

        updateStreamParameters();
        mIsInitialized.store(true);
        return true;
    }
    
    return false;
}

void OboeAudioRenderer::shutdown() {
    if (mAudioStream) {
        if (mIsStreamStarted.load()) {
            mAudioStream->stop();
        }
        mAudioStream->close();
        mAudioStream.reset();
    }
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        return;
    }
    mSampleRate.store(sampleRate);
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) {
        return;
    }
    mBufferSize.store(bufferSize);
}

void OboeAudioRenderer::setVolume(float volume) {
    float clampedVolume = std::clamp(volume, 0.0f, 1.0f);
    mVolume.store(clampedVolume);
}

void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames) {
    // 检查初始化状态，如果未初始化则尝试初始化
    if (!mIsInitialized.load()) {
        if (!initialize()) {
            return;
        }
    }

    if (!data || numFrames <= 0) {
        return;
    }

    // 首次写入时启动流
    if (!mIsStreamStarted.load()) {
        std::lock_guard<std::mutex> lock(mInitMutex);
        if (!mIsStreamStarted.load()) {
            oboe::Result result = mAudioStream->requestStart();
            if (result == oboe::Result::OK) {
                mIsStreamStarted.store(true);
                // 移除了预填充静音代码
            } else {
                return;
            }
        }
    }

    int32_t channelCount = mChannelCount.load();
    size_t totalSamples = numFrames * channelCount;
    
    // 直接写入，不检查溢出（由调用方控制流量）
    mRingBuffer->write(data, totalSamples);
}

void OboeAudioRenderer::clearBuffer() {
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
}

size_t OboeAudioRenderer::getBufferedFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    return mRingBuffer->available() / channelCount;
}

size_t OboeAudioRenderer::getAvailableFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    return mRingBuffer->availableForWrite() / channelCount;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    // 获取当前参数值
    int32_t channelCount = mChannelCount.load();
    oboe::AudioFormat audioFormat = mAudioFormat.load();
    float volume = mVolume.load();

    // 流未启动 → 静音
    if (!mIsStreamStarted.load()) {
        if (audioFormat == oboe::AudioFormat::I16) {
            memset(audioData, 0, numFrames * channelCount * sizeof(int16_t));
        } else {
            memset(audioData, 0, numFrames * channelCount * sizeof(float));
        }
        return oboe::DataCallbackResult::Continue;
    }

    size_t totalSamples = numFrames * channelCount;
    
    if (audioFormat == oboe::AudioFormat::I16) {
        int16_t* output = static_cast<int16_t*>(audioData);
        std::vector<float> floatData(totalSamples);
        
        size_t read = mRingBuffer->read(floatData.data(), totalSamples);
        
        if (read < totalSamples) {
            std::fill(floatData.begin() + read, floatData.end(), 0.0f);
        }
        
        for (size_t i = 0; i < totalSamples; i++) {
            float sample = floatData[i] * volume;
            sample = std::clamp(sample, -1.0f, 1.0f);
            output[i] = static_cast<int16_t>(sample * 32767);
        }
    } else {
        float* output = static_cast<float*>(audioData);
        size_t read = mRingBuffer->read(output, totalSamples);
        
        if (read < totalSamples) {
            std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
        }
        
        if (volume != 1.0f) {
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
            }
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
}
