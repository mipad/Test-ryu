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

#ifndef ALOGW
#define ALOGW(...) __android_log_print(ANDROID_LOG_WARN, "OboeAudio", __VA_ARGS__)
#endif

#ifndef ALOGD
#define ALOGD(...) __android_log_print(ANDROID_LOG_DEBUG, "OboeAudio", __VA_ARGS__)
#endif

// =============== RingBuffer Implementation ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity) 
{
    ALOGD("RingBuffer created with capacity: %zu", capacity);
}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0) return true;

    size_t writeIndex = mWriteIndex.load(std::memory_order_relaxed);
    size_t readIndex = mReadIndex.load(std::memory_order_acquire);

    size_t available = availableForWrite();
    if (count > available) {
        ALOGW("RingBuffer write overflow: requested %zu, available %zu", count, available);
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
    ALOGD("RingBuffer wrote %zu samples, new write index: %zu", count, mWriteIndex.load());
    return true;
}

size_t RingBuffer::read(float* output, size_t count) {
    if (count == 0) return 0;

    size_t writeIndex = mWriteIndex.load(std::memory_order_acquire);
    size_t readIndex = mReadIndex.load(std::memory_order_relaxed);

    size_t availableSamples = available();
    size_t toRead = std::min(count, availableSamples);
    if (toRead == 0) {
        ALOGW("RingBuffer read underflow: requested %zu, available %zu", count, availableSamples);
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

    mReadIndex.store((readIndex + toRead) % mCapacity, std::memory_order_release);
    ALOGD("RingBuffer read %zu samples, new read index: %zu", toRead, mReadIndex.load());
    return toRead;
}

size_t RingBuffer::available() const {
    size_t write = mWriteIndex.load(std::memory_order_acquire);
    size_t read = mReadIndex.load(std::memory_order_relaxed);
    
    if (write >= read) {
        return write - read;
    } else {
        return mCapacity - read + write;
    }
}

size_t RingBuffer::availableForWrite() const {
    size_t write = mWriteIndex.load(std::memory_order_relaxed);
    size_t read = mReadIndex.load(std::memory_order_acquire);
    
    if (write >= read) {
        return mCapacity - write + read - 1;
    } else {
        return read - write - 1;
    }
}

void RingBuffer::clear() {
    mReadIndex.store(0, std::memory_order_relaxed);
    mWriteIndex.store(0, std::memory_order_relaxed);
    ALOGD("RingBuffer cleared");
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>(48000 * 2 * 5)) // 5秒的缓冲
{
    ALOGD("OboeAudioRenderer constructor called");
}

OboeAudioRenderer::~OboeAudioRenderer() {
    ALOGD("OboeAudioRenderer destructor called");
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::openStreamWithFormat(oboe::AudioFormat format) {
    oboe::AudioStreamBuilder builder;

    // 强制使用 OpenSL ES 后端
    ALOGD("Attempting to open stream with format: %d", format);
    builder.setAudioApi(oboe::AudioApi::OpenSLES);
    
    // 修改为更兼容的模式设置
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::None) // 改为None以提高兼容性
           ->setSharingMode(oboe::SharingMode::Shared) // 改为Shared以提高兼容性
           ->setFormat(format)
           ->setChannelCount(mChannelCount.load(std::memory_order_relaxed))
           ->setSampleRate(mSampleRate.load(std::memory_order_relaxed))
           ->setBufferCapacityInFrames(mBufferSize.load(std::memory_order_relaxed))
           ->setDataCallback(this)
           ->setErrorCallback(this);

    oboe::Result result = builder.openStream(mAudioStream);
    if (result != oboe::Result::OK) {
        ALOGE("Failed to open audio stream with format %d: %s", format, oboe::convertToText(result));
        return false;
    }

    ALOGI("Successfully opened audio stream with format: %d", format);
    return true;
}

void OboeAudioRenderer::updateStreamParameters() {
    if (mAudioStream) {
        mSampleRate.store(mAudioStream->getSampleRate(), std::memory_order_relaxed);
        mBufferSize.store(mAudioStream->getBufferSizeInFrames(), std::memory_order_relaxed);
        mChannelCount.store(mAudioStream->getChannelCount(), std::memory_order_relaxed);
        mAudioFormat.store(mAudioStream->getFormat(), std::memory_order_relaxed);
        
        ALOGI("Stream parameters updated: SampleRate=%d, BufferSize=%d, Channels=%d, Format=%d",
              mSampleRate.load(), mBufferSize.load(), mChannelCount.load(), mAudioFormat.load());
    }
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized.load(std::memory_order_acquire)) {
        ALOGD("OboeAudioRenderer already initialized");
        return true;
    }

    std::lock_guard<std::mutex> lock(mInitMutex);
    if (mIsInitialized.load(std::memory_order_acquire)) {
        ALOGD("OboeAudioRenderer already initialized (double-checked)");
        return true;
    }

    ALOGI("Initializing OboeAudioRenderer...");
    
    // 首先尝试Float格式
    if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
        ALOGW("Float format failed, trying I16 format");
        if (!openStreamWithFormat(oboe::AudioFormat::I16)) {
            ALOGE("All audio format attempts failed");
            return false;
        }
    }

    // 启动音频流
    oboe::Result result = mAudioStream->requestStart();
    if (result != oboe::Result::OK) {
        ALOGE("Failed to start audio stream: %s", oboe::convertToText(result));
        mAudioStream->close();
        mAudioStream.reset();
        return false;
    }

    // 更新实际使用的参数
    updateStreamParameters();
    
    mIsInitialized.store(true, std::memory_order_release);
    ALOGI("OboeAudioRenderer initialized successfully");
    return true;
}

void OboeAudioRenderer::shutdown() {
    ALOGI("Shutting down OboeAudioRenderer");
    if (mAudioStream) {
        ALOGD("Stopping audio stream");
        mAudioStream->stop();
        ALOGD("Closing audio stream");
        mAudioStream->close();
        mAudioStream.reset();
        ALOGD("Audio stream released");
    }
    if (mRingBuffer) {
        ALOGD("Clearing ring buffer");
        mRingBuffer->clear();
    }
    mIsInitialized.store(false, std::memory_order_release);
    ALOGI("OboeAudioRenderer shutdown complete");
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        ALOGW("Invalid sample rate: %d", sampleRate);
        return;
    }
    ALOGD("Setting sample rate to: %d", sampleRate);
    mSampleRate.store(sampleRate, std::memory_order_relaxed);
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) {
        ALOGW("Invalid buffer size: %d", bufferSize);
        return;
    }
    ALOGD("Setting buffer size to: %d", bufferSize);
    mBufferSize.store(bufferSize, std::memory_order_relaxed);
}

void OboeAudioRenderer::setVolume(float volume) {
    float clampedVolume = std::clamp(volume, 0.0f, 1.0f);
    ALOGD("Setting volume to: %.2f", clampedVolume);
    mVolume.store(clampedVolume, std::memory_order_relaxed);
}

void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames) {
    if (!mIsInitialized.load(std::memory_order_acquire)) {
        ALOGW("Attempted to write audio but renderer is not initialized");
        return;
    }

    if (!data || numFrames <= 0) {
        ALOGW("Invalid audio data or frame count: %d", numFrames);
        return;
    }

    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t totalSamples = numFrames * channelCount;
    
    ALOGD("Writing %d frames (%zu samples) to ring buffer", numFrames, totalSamples);
    
    if (!mRingBuffer->write(data, totalSamples)) {
        ALOGW("Ring buffer overflow when writing %zu samples", totalSamples);
        // 静默处理溢出
    } else {
        ALOGD("Successfully wrote %zu samples to ring buffer", totalSamples);
    }
}

void OboeAudioRenderer::clearBuffer() {
    ALOGD("Clearing audio buffer");
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
}

size_t OboeAudioRenderer::getBufferedFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t frames = mRingBuffer->available() / channelCount;
    ALOGD("Buffered frames: %zu", frames);
    return frames;
}

size_t OboeAudioRenderer::getAvailableFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t frames = mRingBuffer->availableForWrite() / channelCount;
    ALOGD("Available frames: %zu", frames);
    return frames;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    if (!mIsInitialized.load(std::memory_order_acquire)) {
        ALOGW("Audio callback called but renderer is not initialized");
        return oboe::DataCallbackResult::Stop;
    }

    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t totalSamples = numFrames * channelCount;
    float volume = mVolume.load(std::memory_order_relaxed);

    oboe::AudioFormat format = mAudioFormat.load(std::memory_order_relaxed);
    
    ALOGD("Audio callback: %d frames, %d channels, format %d, volume %.2f", 
          numFrames, channelCount, format, volume);
    
    if (format == oboe::AudioFormat::I16) {
        int16_t* output = static_cast<int16_t*>(audioData);
        std::vector<float> floatData(totalSamples);
        
        size_t read = mRingBuffer->read(floatData.data(), totalSamples);
        
        if (read < totalSamples) {
            ALOGW("Audio underflow: requested %zu samples, got %zu", totalSamples, read);
            std::fill(floatData.begin() + read, floatData.end(), 0.0f);
        }
        
        for (size_t i = 0; i < totalSamples; i++) {
            float sample = floatData[i] * volume;
            sample = std::clamp(sample, -1.0f, 1.0f);
            output[i] = static_cast<int16_t>(sample * 32767);
        }
        
        ALOGD("Processed %zu samples for I16 output", totalSamples);
    } else {
        float* output = static_cast<float*>(audioData);
        size_t read = mRingBuffer->read(output, totalSamples);
        
        if (read < totalSamples) {
            ALOGW("Audio underflow: requested %zu samples, got %zu", totalSamples, read);
            std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
        }
        
        if (volume != 1.0f) {
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
            }
        }
        
        ALOGD("Processed %zu samples for Float output", totalSamples);
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    ALOGE("Audio error after close: %s", oboe::convertToText(error));
    mIsInitialized.store(false, std::memory_order_release);
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    ALOGE("Audio error before close: %s", oboe::convertToText(error));
    mIsInitialized.store(false, std::memory_order_release);
}
