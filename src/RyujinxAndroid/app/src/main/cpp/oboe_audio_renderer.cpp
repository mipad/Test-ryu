// oboe_audio_renderer.cpp (深度修复版)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <mutex>  
#include <oboe/Oboe.h>

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
        // 缓冲区不足时，丢弃最旧的数据
        size_t toDiscard = std::min(count - available, available);
        mReadIndex.store((readIndex + toDiscard) % mCapacity);
        readIndex = mReadIndex.load();
        available = (readIndex > writeIndex) ? (readIndex - writeIndex - 1) : 
                   (mCapacity - writeIndex + readIndex - 1);
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
    : mRingBuffer(std::make_unique<RingBuffer>((48000 * 2 * 300) / 1000)) // 增加缓冲区大小到300ms
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
    
    const int maxApiRetries = 3;
    oboe::AudioApi audioApis[] = {
        oboe::AudioApi::AAudio,
        oboe::AudioApi::OpenSLES
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
               ->setBufferCapacityInFrames(oboe::DefaultStreamValues::FramesPerBurst * 8) // 增加缓冲区大小
               ->setFramesPerCallback(oboe::DefaultStreamValues::FramesPerBurst) // 设置每回调帧数
               ->setDataCallback(this)
               ->setErrorCallback(this);

        result = builder.openStream(mAudioStream);
        
        if (result == oboe::Result::OK) {
            // 设置缓冲区大小
            mAudioStream->setBufferSizeInFrames(mBufferSize.load());
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
    
    const int maxRetries = 3;
    for (int attempt = 0; attempt < maxRetries; attempt++) {
        if (attempt > 0) {
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        
        // 优先尝试Float格式
        if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
            if (!openStreamWithFormat(oboe::AudioFormat::I16)) {
                continue;
            }
        }

        updateStreamParameters();
        
        // 预填充静音数据
        std::vector<float> silence(mRingBuffer->availableForWrite(), 0.0f);
        if (silence.size() > 0) {
            mRingBuffer->write(silence.data(), silence.size());
        }
        
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
    if (!mIsInitialized.load()) {
        if (!initialize()) {
            return;
        }
    }

    if (!data || numFrames <= 0) {
        return;
    }

    if (!mIsStreamStarted.load()) {
        std::lock_guard<std::mutex> lock(mInitMutex);
        if (!mIsStreamStarted.load()) {
            oboe::Result result = mAudioStream->requestStart();
            if (result == oboe::Result::OK) {
                mIsStreamStarted.store(true);
            } else {
                return;
            }
        }
    }

    int32_t channelCount = mChannelCount.load();
    size_t totalSamples = numFrames * channelCount;
    
    // 分批写入以避免阻塞
    const size_t maxChunkSize = 512 * channelCount;
    for (size_t offset = 0; offset < totalSamples; offset += maxChunkSize) {
        size_t chunkSize = std::min(maxChunkSize, totalSamples - offset);
        mRingBuffer->write(data + offset, chunkSize);
    }
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
    size_t available = mRingBuffer->available();
    return available / channelCount;
}

size_t OboeAudioRenderer::getAvailableFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    size_t availableForWrite = mRingBuffer->availableForWrite();
    return availableForWrite / channelCount;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    int32_t channelCount = mChannelCount.load();
    oboe::AudioFormat audioFormat = mAudioFormat.load();
    float volume = mVolume.load();

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
        
        // 应用平滑处理减少爆音
        for (size_t i = 0; i < totalSamples; i++) {
            float sample = floatData[i] * volume;
            sample = std::clamp(sample, -1.0f, 1.0f);
            
            // 简单的平滑处理
            if (i > 0) {
                sample = (sample + output[i-1] / 32768.0f * 0.1f) / 1.1f;
            }
            
            output[i] = static_cast<int16_t>(sample * 32767);
        }
    } else {
        float* output = static_cast<float*>(audioData);
        size_t read = mRingBuffer->read(output, totalSamples);
        
        if (read < totalSamples) {
            std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
        }
        
        if (volume != 1.0f) {
            // 应用平滑的音量调整
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
                
                // 简单的平滑处理
                if (i > 0) {
                    output[i] = (output[i] + output[i-1] * 0.1f) / 1.1f;
                }
            }
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    
    // 尝试重新初始化
    std::thread([this]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        initialize();
    }).detach();
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    
    // 尝试重新初始化
    std::thread([this]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        initialize();
    }).detach();
}
