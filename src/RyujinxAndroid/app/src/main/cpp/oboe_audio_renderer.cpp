// oboe_audio_renderer.cpp
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <cstring>
#include <algorithm>

#ifndef ALOGE
#define ALOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OboeAudio", __VA_ARGS__)
#endif

#ifndef ALOGI
#define ALOGI(...) __android_log_print(ANDROID_LOG_INFO, "OboeAudio", __VA_ARGS__)
#endif

// =============== RingBuffer Implementation ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity) {}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0) return true;

    size_t writeIndex = mWriteIndex.load(std::memory_order_relaxed);
    size_t readIndex = mReadIndex.load(std::memory_order_acquire);

    size_t available = (readIndex <= writeIndex)
        ? (mCapacity - writeIndex + readIndex - 1)
        : (readIndex - writeIndex - 1);

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

    mWriteIndex.store((writeIndex + count) % mCapacity, std::memory_order_release);
    return true;
}

size_t RingBuffer::read(float* output, size_t count) {
    if (count == 0) return 0;

    size_t writeIndex = mWriteIndex.load(std::memory_order_acquire);
    size_t readIndex = mReadIndex.load(std::memory_order_relaxed);

    size_t available = (writeIndex >= readIndex)
        ? (writeIndex - readIndex)
        : (mCapacity - readIndex + writeIndex);

    size_t toRead = std::min(count, available);
    if (toRead == 0) return 0;

    size_t end = readIndex + toRead;
    if (end <= mCapacity) {
        std::memcpy(output, &mBuffer[readIndex], toRead * sizeof(float));
    } else {
        size_t part1 = mCapacity - readIndex;
        std::memcpy(output, &mBuffer[readIndex], part1 * sizeof(float));
        std::memcpy(output + part1, &mBuffer[0], (toRead - part1) * sizeof(float));
    }

    mReadIndex.store((readIndex + toRead) % mCapacity, std::memory_order_release);
    return toRead;
}

size_t RingBuffer::available() const {
    size_t write = mWriteIndex.load(std::memory_order_relaxed);
    size_t read = mReadIndex.load(std::memory_order_relaxed);
    return (write >= read) ? (write - read) : (mCapacity - read + write);
}

void RingBuffer::clear() {
    mReadIndex.store(0, std::memory_order_relaxed);
    mWriteIndex.store(0, std::memory_order_relaxed);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>(48000 * 2 * 5)) { // 5秒容量
}

OboeAudioRenderer::~OboeAudioRenderer() {
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized.load(std::memory_order_acquire)) {
        return true;
    }

    std::lock_guard<std::mutex> lock(mInitMutex);
    if (mIsInitialized.load(std::memory_order_acquire)) {
        return true;
    }

    // 创建构建器并设置参数
    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setFormat(oboe::AudioFormat::Float)
           ->setChannelCount(mChannelCount)
           ->setSampleRate(mSampleRate)
           ->setBufferCapacityInFrames(mBufferSize) // 使用正确的API名称
           ->setDataCallback(this)
           ->setErrorCallback(this);

    // 打开音频流
    oboe::Result result = builder.openStream(mAudioStream);
    if (result != oboe::Result::OK) {
        ALOGE("Failed to open Oboe stream: %s", oboe::convertToText(result));
        return false;
    }

    // 启动音频流
    result = mAudioStream->requestStart();
    if (result != oboe::Result::OK) {
        ALOGE("Failed to start Oboe stream: %s", oboe::convertToText(result));
        mAudioStream->close();
        mAudioStream.reset();
        return false;
    }

    // 更新实际使用的参数
    mSampleRate = mAudioStream->getSampleRate();
    mBufferSize = mAudioStream->getBufferSizeInFrames();
    
    mIsInitialized.store(true, std::memory_order_release);
    ALOGI("Oboe stream started: SR=%d, BufSize=%d, Channels=%d",
          mSampleRate, mBufferSize, mAudioStream->getChannelCount());

    return true;
}

void OboeAudioRenderer::shutdown() {
    if (mAudioStream) {
        mAudioStream->stop();
        mAudioStream->close();
        mAudioStream.reset();
    }
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
    mIsInitialized.store(false, std::memory_order_release);
    ALOGI("Oboe stream shutdown");
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) return;
    mSampleRate = sampleRate;
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) return;
    mBufferSize = bufferSize;
}

void OboeAudioRenderer::setVolume(float volume) {
    mVolume.store(volume, std::memory_order_relaxed);
}

void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames) {
    if (!mIsInitialized.load(std::memory_order_acquire)) {
        ALOGE("writeAudio: Renderer not initialized!");
        return;
    }

    if (!data || numFrames <= 0) return;

    size_t totalSamples = numFrames * mChannelCount;
    if (!mRingBuffer->write(data, totalSamples)) {
        ALOGE("Audio buffer overflow! Dropping %d frames", numFrames);
    }
}

void OboeAudioRenderer::clearBuffer() {
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
}

size_t OboeAudioRenderer::getBufferedFrames() const {
    if (!mRingBuffer) return 0;
    return mRingBuffer->available() / mChannelCount;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    if (!mIsInitialized.load(std::memory_order_acquire)) {
        return oboe::DataCallbackResult::Stop;
    }

    float* output = static_cast<float*>(audioData);
    size_t totalSamples = numFrames * mChannelCount;

    size_t read = mRingBuffer->read(output, totalSamples);
    float volume = mVolume.load(std::memory_order_relaxed);

    if (read < totalSamples) {
        if (read > 0 && volume != 1.0f) {
            for (size_t i = 0; i < read; i++) {
                output[i] *= volume;
            }
        }
        std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
    } else if (volume != 1.0f) {
        for (size_t i = 0; i < totalSamples; i++) {
            output[i] *= volume;
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    ALOGE("Oboe error after close: %s", oboe::convertToText(error));
    mIsInitialized.store(false, std::memory_order_release);
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    ALOGE("Oboe error before close: %s", oboe::convertToText(error));
    mIsInitialized.store(false, std::memory_order_release);
}
