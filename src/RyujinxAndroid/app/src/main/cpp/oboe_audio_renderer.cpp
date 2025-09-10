// oboe_audio_renderer.cpp (终极修复版：高质量采样率转换 + 动态缓冲 + 稳定噪声整形)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <mutex>
#include <cmath>
#include <limits>
#include <android/log.h>

#define LOG_TAG "OboeAudio"
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// =============== 高质量 Cubic 插值采样率转换器 ===============
class SampleRateConverter {
private:
    float mLastSamples[4] = {0}; // 保持4个样本用于cubic插值
    float mPosition = 0.0f;
    float mRatio = 1.0f;
    size_t mWriteIndex = 0;
    bool mHasEnoughSamples = false;

    // Cubic 插值核心函数
    static float cubicInterpolate(float y0, float y1, float y2, float y3, float mu) {
        float mu2 = mu * mu;
        float a0 = y3 - y2 - y0 + y1;
        float a1 = y0 - y1 - a0;
        float a2 = y2 - y0;
        float a3 = y1;
        return a0 * mu * mu2 + a1 * mu2 + a2 * mu + a3;
    }

public:
    SampleRateConverter() = default;

    void setRatio(float inputRate, float outputRate) {
        if (outputRate <= 0) {
            LOGE("Invalid output rate: %f", outputRate);
            return;
        }
        mRatio = inputRate / outputRate;
        reset();
    }

    void reset() {
        std::fill(std::begin(mLastSamples), std::end(mLastSamples), 0.0f);
        mPosition = 0.0f;
        mWriteIndex = 0;
        mHasEnoughSamples = false;
    }

    // 高质量 cubic 插值重采样
    size_t convert(const float* input, size_t inputSize, float* output, size_t outputSize) {
        if (!input || !output || inputSize == 0 || outputSize == 0 || mRatio <= 0) {
            return 0;
        }

        size_t outputIndex = 0;

        for (size_t i = 0; i < inputSize && outputIndex < outputSize; ++i) {
            // 填充环形缓冲
            mLastSamples[mWriteIndex] = input[i];
            mWriteIndex = (mWriteIndex + 1) % 4;

            if (i >= 3) {
                mHasEnoughSamples = true;
            }

            // 当有至少4个样本时开始插值
            if (mHasEnoughSamples) {
                while (mPosition < 1.0f && outputIndex < outputSize) {
                    int idx = (mWriteIndex + 4 - 4) % 4; // 最旧样本索引
                    float y0 = mLastSamples[idx];
                    float y1 = mLastSamples[(idx + 1) % 4];
                    float y2 = mLastSamples[(idx + 2) % 4];
                    float y3 = mLastSamples[(idx + 3) % 4];

                    float sample = cubicInterpolate(y0, y1, y2, y3, mPosition);
                    output[outputIndex++] = sample;

                    mPosition += mRatio;
                }

                // 减去已消耗的整数部分
                if (mPosition >= 1.0f) {
                    mPosition -= 1.0f;
                }
            }
        }

        return outputIndex;
    }
};

// =============== 稳定版噪声整形器 (仅用于 I16 输出) ===============
class NoiseShaper {
private:
    std::mutex mMutex;
    float mHistory[3] = {0};
    const float kFeedback = 0.95f; // 保守反馈系数，避免不稳定

public:
    void reset() {
        std::lock_guard<std::mutex> lock(mMutex);
        mHistory[0] = mHistory[1] = mHistory[2] = 0.0f;
    }

    // 仅在量化前调用！返回整形后的 float，最终量化在外部完成
    float process(float input) {
        std::lock_guard<std::mutex> lock(mMutex);

        if (std::isnan(input) || std::isinf(input)) {
            return 0.0f;
        }

        // 添加历史误差
        float shaped = input + mHistory[0];
        shaped = std::clamp(shaped, -1.0f, 1.0f); // 防止溢出

        // 计算量化误差（量化在外部进行）
        float quantized = std::round(shaped * 32767.0f) / 32767.0f;
        float error = shaped - quantized;

        // 一阶反馈更新历史
        mHistory[2] = mHistory[1];
        mHistory[1] = mHistory[0];
        mHistory[0] = error * kFeedback;

        return quantized;
    }
};

// =============== RingBuffer 实现 (带动态水位日志) ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity), mReadIndex(0), mWriteIndex(0)
{
    LOGI("RingBuffer created with capacity: %zu samples", capacity);
}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0 || !data) return true;

    std::lock_guard<std::mutex> lock(mMutex);
    size_t writeIndex = mWriteIndex.load();
    size_t readIndex = mReadIndex.load();

    size_t available = (readIndex > writeIndex) ? (readIndex - writeIndex - 1) :
                     (mCapacity - writeIndex + readIndex - 1);

    if (count > available) {
        LOGW("RingBuffer overflow: requested %zu, available %zu", count, available);
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
    if (count == 0 || !output) return 0;

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
    return (write >= read) ? (write - read) : (mCapacity - read + write);
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
    LOGI("RingBuffer cleared");
}

// =============== OboeAudioRenderer 实现 (带动态缓冲监控) ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>((48000 * 2 * 250) / 1000)), // 250ms @ 48kHz stereo
      mNoiseShaper(std::make_unique<NoiseShaper>()),
      mSampleRateConverter(std::make_unique<SampleRateConverter>()),
      mLastBufferLevel(0),
      mUnderrunCount(0),
      mTotalFramesWritten(0)
{
    LOGI("OboeAudioRenderer constructed");
}

OboeAudioRenderer::~OboeAudioRenderer() {
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

void OboeAudioRenderer::setNoiseShapingEnabled(bool enabled) {
    mNoiseShapingEnabled.store(enabled);
    LOGI("Noise shaping %s", enabled ? "enabled" : "disabled");
    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
}

bool OboeAudioRenderer::openStreamWithFormat(oboe::AudioFormat format) {
    oboe::AudioStreamBuilder builder;

    const int maxApiRetries = 2;
    oboe::AudioApi audioApis[] = {
        oboe::AudioApi::AAudio,
        oboe::AudioApi::OpenSLES
    };

    oboe::Result result = oboe::Result::ErrorInternal;

    for (int apiAttempt = 0; apiAttempt < maxApiRetries; apiAttempt++) {
        builder.setAudioApi(audioApis[apiAttempt]);

        builder.setDirection(oboe::Direction::Output)
               ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
               ->setSharingMode(oboe::SharingMode::Exclusive)
               ->setFormat(format)
               ->setChannelCount(mChannelCount.load())
               ->setSampleRate(mSampleRate.load())
               ->setBufferCapacityInFrames(oboe::DefaultStreamValues::FramesPerBurst * 4) // 增大缓冲
               ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
               ->setDataCallback(this)
               ->setErrorCallback(this);

        result = builder.openStream(mAudioStream);

        if (result == oboe::Result::OK) {
            LOGI("Stream opened with API: %d, Format: %d", audioApis[apiAttempt], format);
            break;
        } else {
            LOGW("Failed to open stream with API %d, format %d: %s",
                 audioApis[apiAttempt], format, oboe::convertToText(result));
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

        mSampleRateConverter->setRatio(48000.0f, static_cast<float>(mSampleRate.load()));

        LOGI("Stream parameters updated: SR=%d, BufSize=%d, Channels=%d, Format=%d",
             mSampleRate.load(), mBufferSize.load(), mChannelCount.load(), mAudioFormat.load());
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

        if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
            if (!openStreamWithFormat(oboee::AudioFormat::I16)) {
                continue;
            }
        }

        updateStreamParameters();
        mIsInitialized.store(true);
        mUnderrunCount = 0;
        mTotalFramesWritten = 0;
        LOGI("Oboe audio initialized successfully");
        return true;
    }

    LOGE("Failed to initialize Oboe audio after %d attempts", maxRetries);
    return false;
}

void OboeAudioRenderer::shutdown() {
    LOGI("Shutting down Oboe audio");

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
    mUnderrunCount = 0;
    mTotalFramesWritten = 0;

    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    if (mSampleRateConverter) {
        mSampleRateConverter->reset();
    }

    LOGI("Oboe audio shutdown complete");
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        LOGE("Invalid sample rate: %d", sampleRate);
        return;
    }
    mSampleRate.store(sampleRate);
    if (mSampleRateConverter) {
        mSampleRateConverter->setRatio(48000.0f, static_cast<float>(sampleRate));
        LOGI("Sample rate set to %d Hz", sampleRate);
    }
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) {
        LOGE("Invalid buffer size: %d", bufferSize);
        return;
    }
    mBufferSize.store(bufferSize);
    LOGI("Buffer size set to %d frames", bufferSize);
}

void OboeAudioRenderer::setVolume(float volume) {
    float clampedVolume = std::clamp(volume, 0.0f, 1.0f);
    mVolume.store(clampedVolume);
    LOGI("Volume set to %.2f", clampedVolume);
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
                LOGI("Audio stream started");
            } else {
                LOGE("Failed to start audio stream: %s", oboe::convertToText(result));
                return;
            }
        }
    }

    int32_t channelCount = mChannelCount.load();
    size_t totalSamples = numFrames * channelCount;

    // 动态采样率转换
    if (mSampleRate.load() != 48000) {
        std::vector<float> convertedSamples(totalSamples * 2); // 预留空间
        size_t convertedCount = mSampleRateConverter->convert(
            data, totalSamples, convertedSamples.data(), convertedSamples.size());

        if (convertedCount > 0) {
            if (!mRingBuffer->write(convertedSamples.data(), convertedCount)) {
                LOGW("RingBuffer write failed during sample rate conversion");
            }
        }
    } else {
        if (!mRingBuffer->write(data, totalSamples)) {
            LOGW("RingBuffer write failed");
        }
    }

    mTotalFramesWritten += numFrames;
}

void OboeAudioRenderer::clearBuffer() {
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    if (mSampleRateConverter) {
        mSampleRateConverter->reset();
    }
    LOGI("Buffers cleared");
}

size_t OboeAudioRenderer::getBufferedFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    if (channelCount == 0) return 0;
    return mRingBuffer->available() / channelCount;
}

size_t OboeAudioRenderer::getAvailableFrames() const {
    if (!mRingBuffer) {
        return 0;
    }
    int32_t channelCount = mChannelCount.load();
    if (channelCount == 0) return 0;
    return mRingBuffer->availableForWrite() / channelCount;
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {

    int32_t channelCount = mChannelCount.load();
    oboe::AudioFormat audioFormat = mAudioFormat.load();
    float volume = mVolume.load();
    bool noiseShapingEnabled = mNoiseShapingEnabled.load();

    size_t totalSamples = numFrames * channelCount;

    // 检查缓冲区水位
    size_t bufferedSamples = mRingBuffer->available();
    size_t bufferedFrames = bufferedSamples / channelCount;

    // 动态日志：每1000帧或水位变化>20%时打印
    if (mTotalFramesWritten % 1000 == 0 ||
        (bufferedFrames > 0 && std::abs((int)bufferedFrames - (int)mLastBufferLevel) > (int)(mLastBufferLevel * 0.2))) {
        LOGD("Buffer level: %zu frames (target: %d), Underruns: %zu",
             bufferedFrames, mBufferSize.load(), mUnderrunCount.load());
        mLastBufferLevel = bufferedFrames;
    }

    // 欠载保护
    if (bufferedSamples < totalSamples) {
        mUnderrunCount++;
        LOGW("Audio underrun #%zu! Buffered: %zu samples, Needed: %zu",
             mUnderrunCount.load(), bufferedSamples, totalSamples);

        if (audioFormat == oboe::AudioFormat::I16) {
            memset(audioData, 0, numFrames * channelCount * sizeof(int16_t));
        } else {
            memset(audioData, 0, numFrames * channelCount * sizeof(float));
        }
        return oboe::DataCallbackResult::Continue;
    }

    if (audioFormat == oboe::AudioFormat::I16) {
        int16_t* output = static_cast<int16_t*>(audioData);
        std::vector<float> floatData(totalSamples);

        size_t read = mRingBuffer->read(floatData.data(), totalSamples);
        if (read < totalSamples) {
            std::fill(floatData.begin() + read, floatData.end(), 0.0f);
        }

        // 应用音量 + 噪声整形（仅I16）
        for (size_t i = 0; i < totalSamples; i++) {
            float sample = floatData[i] * volume;
            sample = std::clamp(sample, -1.0f, 1.0f);

            if (noiseShapingEnabled && mNoiseShaper) {
                sample = mNoiseShaper->process(sample);
            }

            output[i] = static_cast<int16_t>(sample * 32767.0f);
        }
    } else {
        float* output = static_cast<float*>(audioData);
        size_t read = mRingBuffer->read(output, totalSamples);

        if (read < totalSamples) {
            std::memset(output + read, 0, (totalSamples - read) * sizeof(float));
        }

        // 只应用音量（Float格式不需要噪声整形）
        if (volume != 1.0f) {
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
            }
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("Oboe stream error after close: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    if (mNoiseShaper) mNoiseShaper->reset();
    if (mSampleRateConverter) mSampleRateConverter->reset();
}

void OboeAudioRenderer::onErrorBeforeClose(oboee::AudioStream* audioStream, oboe::Result error) {
    LOGE("Oboe stream error before close: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    if (mNoiseShaper) mNoiseShaper->reset();
    if (mSampleRateConverter) mSampleRateConverter->reset();
}
