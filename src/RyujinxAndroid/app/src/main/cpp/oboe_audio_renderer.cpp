// oboe_audio_renderer.cpp (优化版)
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>

// 声明 logToFile 函数
extern "C" void logToFile(int level, const char* tag, const char* format, ...);

// =============== RingBuffer Implementation ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity) 
{
    logToFile(3, "OboeAudio", "RingBuffer created with capacity: %zu", capacity);
}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0) return true;

    size_t writeIndex = mWriteIndex.load(std::memory_order_relaxed);
    size_t readIndex = mReadIndex.load(std::memory_order_acquire);

    size_t available = availableForWrite();
    if (count > available) {
        logToFile(5, "OboeAudio", "RingBuffer write overflow: requested %zu, available %zu", count, available);
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
    logToFile(3, "OboeAudio", "RingBuffer wrote %zu samples, new write index: %zu", count, mWriteIndex.load());
    return true;
}

size_t RingBuffer::read(float* output, size_t count) {
    if (count == 0) return 0;

    size_t writeIndex = mWriteIndex.load(std::memory_order_acquire);
    size_t readIndex = mReadIndex.load(std::memory_order_relaxed);

    size_t availableSamples = available();
    size_t toRead = std::min(count, availableSamples);
    if (toRead == 0) {
        logToFile(5, "OboeAudio", "RingBuffer read underflow: requested %zu, available %zu", count, availableSamples);
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
    logToFile(3, "OboeAudio", "RingBuffer read %zu samples, new read index: %zu", toRead, mReadIndex.load());
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
    logToFile(3, "OboeAudio", "RingBuffer cleared");
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>(48000 * 2 * 5)) // 减少到5秒缓冲
{
    logToFile(3, "OboeAudio", "OboeAudioRenderer constructor called");
}

OboeAudioRenderer::~OboeAudioRenderer() {
    logToFile(3, "OboeAudio", "OboeAudioRenderer destructor called");
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::openStreamWithFormat(oboe::AudioFormat format) {
    oboe::AudioStreamBuilder builder;

    logToFile(3, "OboeAudio", "Attempting to open stream with format: %d", format);
    // 不再强制使用OpenSL ES，让Oboe自动选择最佳API
    
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency) // 使用低延迟模式
           ->setSharingMode(oboe::SharingMode::Shared)
           ->setFormat(format)
           ->setChannelCount(mChannelCount.load(std::memory_order_relaxed))
           ->setSampleRate(mSampleRate.load(std::memory_order_relaxed))
           ->setBufferCapacityInFrames(mBufferSize.load(std::memory_order_relaxed))
           ->setDataCallback(this)
           ->setErrorCallback(this);

    oboe::Result result = builder.openStream(mAudioStream);
    if (result != oboe::Result::OK) {
        logToFile(6, "OboeAudio", "Failed to open audio stream with format %d: %s", format, oboe::convertToText(result));
        return false;
    }

    logToFile(4, "OboeAudio", "Successfully opened audio stream with format: %d", format);
    logToFile(4, "OboeAudio", "Using audio API: %s", oboe::convertToText(mAudioStream->getAudioApi()));
    return true;
}

void OboeAudioRenderer::updateStreamParameters() {
    if (mAudioStream) {
        mSampleRate.store(mAudioStream->getSampleRate(), std::memory_order_relaxed);
        mBufferSize.store(mAudioStream->getBufferSizeInFrames(), std::memory_order_relaxed);
        mChannelCount.store(mAudioStream->getChannelCount(), std::memory_order_relaxed);
        mAudioFormat.store(mAudioStream->getFormat(), std::memory_order_relaxed);
        
        logToFile(4, "OboeAudio", "Stream parameters updated: SampleRate=%d, BufferSize=%d, Channels=%d, Format=%d",
              mSampleRate.load(), mBufferSize.load(), mChannelCount.load(), mAudioFormat.load());
    }
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized.load(std::memory_order_acquire)) {
        logToFile(3, "OboeAudio", "OboeAudioRenderer already initialized");
        return true;
    }

    std::lock_guard<std::mutex> lock(mInitMutex);
    if (mIsInitialized.load(std::memory_order_acquire)) {
        logToFile(3, "OboeAudio", "OboeAudioRenderer already initialized (double-checked)");
        return true;
    }

    logToFile(4, "OboeAudio", "Initializing OboeAudioRenderer (stream not started yet)...");
    
    const int maxRetries = 3;
    for (int attempt = 0; attempt < maxRetries; attempt++) {
        if (attempt > 0) {
            logToFile(4, "OboeAudio", "Retry attempt %d/%d", attempt + 1, maxRetries);
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        
        if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
            logToFile(5, "OboeAudio", "Float format failed, trying I16 format");
            if (!openStreamWithFormat(oboe::AudioFormat::I16)) {
                logToFile(6, "OboeAudio", "All audio format attempts failed");
                continue;
            }
        }

        updateStreamParameters();
        mIsInitialized.store(true, std::memory_order_release);
        logToFile(4, "OboeAudio", "Oboe stream opened (not started) on attempt %d", attempt + 1);
        return true;
    }
    
    logToFile(6, "OboeAudio", "All initialization attempts failed");
    return false;
}

void OboeAudioRenderer::shutdown() {
    logToFile(4, "OboeAudio", "Shutting down OboeAudioRenderer");
    if (mAudioStream) {
        if (mIsStreamStarted.load(std::memory_order_acquire)) {
            logToFile(3, "OboeAudio", "Stopping audio stream");
            mAudioStream->stop();
        }
        logToFile(3, "OboeAudio", "Closing audio stream");
        mAudioStream->close();
        mAudioStream.reset();
        logToFile(3, "OboeAudio", "Audio stream released");
    }
    if (mRingBuffer) {
        logToFile(3, "OboeAudio", "Clearing ring buffer");
        mRingBuffer->clear();
    }
    mIsStreamStarted.store(false, std::memory_order_release);
    mIsInitialized.store(false, std::memory_order_release);
    logToFile(4, "OboeAudio", "OboeAudioRenderer shutdown complete");
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        logToFile(5, "OboeAudio", "Invalid sample rate: %d", sampleRate);
        return;
    }
    logToFile(3, "OboeAudio", "Setting sample rate to: %d", sampleRate);
    mSampleRate.store(sampleRate, std::memory_order_relaxed);
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) {
        logToFile(5, "OboeAudio", "Invalid buffer size: %d", bufferSize);
        return;
    }
    logToFile(3, "OboeAudio", "Setting buffer size to: %d", bufferSize);
    mBufferSize.store(bufferSize, std::memory_order_relaxed);
}

void OboeAudioRenderer::setVolume(float volume) {
    float clampedVolume = std::clamp(volume, 0.0f, 1.0f);
    logToFile(3, "OboeAudio", "Setting volume to: %.2f", clampedVolume);
    mVolume.store(clampedVolume, std::memory_order_relaxed);
}

void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames) {
    if (!mIsInitialized.load(std::memory_order_acquire)) {
        logToFile(5, "OboeAudio", "Attempted to write audio but renderer is not initialized");
        return;
    }

    if (!data || numFrames <= 0) {
        logToFile(5, "OboeAudio", "Invalid audio data or frame count: %d", numFrames);
        return;
    }

    // 首次写入时启动流
    if (!mIsStreamStarted.load(std::memory_order_acquire)) {
        std::lock_guard<std::mutex> lock(mInitMutex);
        if (!mIsStreamStarted.load(std::memory_order_acquire)) {
            oboe::Result result = mAudioStream->requestStart();
            if (result == oboe::Result::OK) {
                mIsStreamStarted.store(true, std::memory_order_release);
                logToFile(4, "OboeAudio", "Audio stream started on first write!");
                
                // 减少预填充到100ms静音
                int32_t sampleRate = mSampleRate.load();
                int32_t frames = sampleRate / 10; // 100ms
                int32_t channels = mChannelCount.load();
                std::vector<float> silence(frames * channels, 0.0f);
                mRingBuffer->write(silence.data(), silence.size());
                logToFile(4, "OboeAudio", "Pre-filled %d frames of silence", frames);
            } else {
                logToFile(6, "OboeAudio", "Failed to start audio stream: %s", oboe::convertToText(result));
                return;
            }
        }
    }

    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t totalSamples = numFrames * channelCount;
    
    logToFile(3, "OboeAudio", "Writing %d frames (%zu samples) to ring buffer", numFrames, totalSamples);
    
    if (!mRingBuffer->write(data, totalSamples)) {
        logToFile(5, "OboeAudio", "Ring buffer overflow when writing %zu samples", totalSamples);
    } else {
        logToFile(3, "OboeAudio", "Successfully wrote %zu samples to ring buffer", totalSamples);
    }
}

void OboeAudioRenderer::clearBuffer() {
    logToFile(3, "OboeAudio", "Clearing audio buffer");
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
    logToFile(3, "OboeAudio", "Buffered frames: %zu", frames);
    return frames;
}

size_t OboeAudioRenderer::getAvailableFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t frames = mRingBuffer->availableForWrite() / channelCount;
    logToFile(3, "OboeAudio", "Available frames: %zu", frames);
    return frames;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    // 流未启动 → 静音，无警告
    if (!mIsStreamStarted.load(std::memory_order_acquire)) {
        oboe::AudioFormat format = mAudioFormat.load(std::memory_order_relaxed);
        int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
        if (format == oboe::AudioFormat::I16) {
            memset(audioData, 0, numFrames * channelCount * sizeof(int16_t));
        } else {
            memset(audioData, 0, numFrames * channelCount * sizeof(float));
        }
        return oboe::DataCallbackResult::Continue;
    }

    int32_t channelCount = mChannelCount.load(std::memory_order_relaxed);
    size_t totalSamples = numFrames * channelCount;
    float volume = mVolume.load(std::memory_order_relaxed);

    oboe::AudioFormat format = mAudioFormat.load(std::memory_order_relaxed);
    
    logToFile(3, "OboeAudio", "Audio callback: %d frames, %d channels, format %d, volume %.2f", 
          numFrames, channelCount, format, volume);
    
    if (format == oboe::AudioFormat::I16) {
        int16_t* output = static_cast<int16_t*>(audioData);
        std::vector<float> floatData(totalSamples);
        
        size_t read = mRingBuffer->read(floatData.data(), totalSamples);
        
        if (read < totalSamples) {
            logToFile(5, "OboeAudio", "Audio underflow: requested %zu samples, got %zu", totalSamples, read);
            std::fill(floatData.begin() + read, floatData.end(), 0.0f);
        }
        
        for (size_t i = 0; i < totalSamples; i++) {
            float sample = floatData[i] * volume;
            sample = std::clamp(sample, -1.0f, 1.0f);
            output[i] = static_cast<int16_t>(sample * 32767);
        }
        
        logToFile(3, "OboeAudio", "Processed %zu samples for I16 output", totalSamples);
    } else {
        float* output = static_cast<float*>(audioData);
        size_t read = mRingBuffer->read(output, totalSamples);
        
        if (read < totalSamples) {
            logToFile(5, "OboeAudio", "Audio underflow: requested %zu samples, got %zu", totalSamples, read);
            std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
        }
        
        if (volume != 1.0f) {
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
            }
        }
        
        logToFile(3, "OboeAudio", "Processed %zu samples for Float output", totalSamples);
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    logToFile(6, "OboeAudio", "Audio error after close: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false, std::memory_order_release);
    mIsInitialized.store(false, std::memory_order_release);
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    logToFile(6, "OboeAudio", "Audio error before close: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false, std::memory_order_release);
    mIsInitialized.store(false, std::memory_order_release);
}
