// oboe_audio_renderer.cpp (修复版)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <mutex>
#include <cmath>
#include <limits>
#include <vector>
#include <android/log.h>

#define LOG_TAG "OboeAudio"
#define LOGD(...) __android_log_print(ANDROID_LOG_DEBUG, LOG_TAG, __VA_ARGS__)
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// =============== 高质量 Cubic 插值采样率转换器 ===============
float SampleRateConverter::cubicInterpolate(float y0, float y1, float y2, float y3, float mu) {
    float mu2 = mu * mu;
    float a0 = y3 - y2 - y0 + y1;
    float a1 = y0 - y1 - a0;
    float a2 = y2 - y0;
    float a3 = y1;
    return a0 * mu * mu2 + a1 * mu2 + a2 * mu + a3;
}

void SampleRateConverter::setRatio(float inputRate, float outputRate) {
    if (outputRate <= 0 || inputRate <= 0) {
        LOGE("Invalid rates: input=%f, output=%f", inputRate, outputRate);
        return;
    }
    mRatio = inputRate / outputRate;
    LOGI("SampleRateConverter ratio set: %.2f (input=%f, output=%f)", mRatio, inputRate, outputRate);
    reset();
}

void SampleRateConverter::reset() {
    std::fill(std::begin(mLastSamples), std::end(mLastSamples), 0.0f);
    mPosition = 0.0f;
    mWriteIndex = 0;
    mHasEnoughSamples = false;
}

size_t SampleRateConverter::convert(const float* input, size_t inputSize, float* output, size_t outputSize) {
    if (!input || !output || inputSize == 0 || outputSize == 0 || mRatio <= 0) {
        return 0;
    }

    size_t outputIndex = 0;

    for (size_t i = 0; i < inputSize && outputIndex < outputSize; ++i) {
        mLastSamples[mWriteIndex] = input[i];
        mWriteIndex = (mWriteIndex + 1) % 4;

        if (i >= 3) {
            mHasEnoughSamples = true;
        }

        if (mHasEnoughSamples) {
            while (mPosition < 1.0f && outputIndex < outputSize) {
                int idx = (mWriteIndex + 4 - 4) % 4;
                float y0 = mLastSamples[idx];
                float y1 = mLastSamples[(idx + 1) % 4];
                float y2 = mLastSamples[(idx + 2) % 4];
                float y3 = mLastSamples[(idx + 3) % 4];

                float sample = cubicInterpolate(y0, y1, y2, y3, mPosition);
                output[outputIndex++] = sample;

                mPosition += mRatio;
            }

            if (mPosition >= 1.0f) {
                mPosition -= 1.0f;
            }
        }
    }

    return outputIndex;
}

// =============== 稳定版噪声整形器 (默认关闭) ===============
void NoiseShaper::reset() {
    std::lock_guard<std::mutex> lock(mMutex);
    mHistory[0] = mHistory[1] = mHistory[2] = 0.0f;
}

float NoiseShaper::process(float input) {
    std::lock_guard<std::mutex> lock(mMutex);

    if (std::isnan(input) || std::isinf(input)) {
        return 0.0f;
    }

    float shaped = input + mHistory[0];
    shaped = std::clamp(shaped, -1.0f, 1.0f);

    float quantized = std::round(shaped * 32767.0f) / 32767.0f;
    float error = shaped - quantized;

    mHistory[2] = mHistory[1];
    mHistory[1] = mHistory[0];
    mHistory[0] = error * 0.95f;

    return quantized;
}

// =============== RingBuffer 实现 ===============
RingBuffer::RingBuffer(size_t capacity)
    : mCapacity(capacity), mBuffer(capacity), mReadIndex(0), mWriteIndex(0)
{
}

bool RingBuffer::write(const float* data, size_t count) {
    if (count == 0 || !data) return true;

    std::lock_guard<std::mutex> lock(mMutex);
    size_t writeIndex = mWriteIndex.load();
    size_t readIndex = mReadIndex.load();

    size_t available = (readIndex > writeIndex) ? (readIndex - writeIndex - 1) :
                     (mCapacity - writeIndex + readIndex - 1);

    if (count > available) {
        LOGW("RingBuffer: Not enough space. Available: %zu, Requested: %zu", available, count);
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
}

// =============== OboeAudioRenderer 实现 ===============
OboeAudioRenderer::OboeAudioRenderer()
    : mRingBuffer(std::make_unique<RingBuffer>((48000 * 2 * 500) / 1000)), // 增加缓冲区到500ms
      mLastBufferLevel(0),
      mUnderrunCount(0),
      mTotalFramesWritten(0)
{
    // 为每个通道创建采样率转换器
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i] = std::make_unique<SampleRateConverter>();
    }
    
    // 创建噪声整形器
    mNoiseShaper = std::make_unique<NoiseShaper>();
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
    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    LOGI("Noise shaping %s", enabled ? "enabled" : "disabled");
}

// 设置的是 Oboe 输出流的声道数
void OboeAudioRenderer::setChannelCount(int32_t channelCount) {
    mChannelCount.store(channelCount);
    LOGI("Output channel count set to: %d", channelCount);
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

        // 设置缓冲区容量为设备的最佳值
        builder.setDirection(oboe::Direction::Output)
               ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
               ->setSharingMode(oboe::SharingMode::Shared)  // 改为共享模式，兼容性更好
               ->setFormat(format)
               ->setChannelCount(mChannelCount.load())
               ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
               ->setDataCallback(this)
               ->setErrorCallback(this);

        result = builder.openStream(mAudioStream);

        if (result == oboe::Result::OK) {
            LOGI("Successfully opened Oboe stream with %s API", 
                 (apiAttempt == 0) ? "AAudio" : "OpenSL ES");
            break;
        } else {
            LOGW("Failed to open stream with %s API: %s", 
                 (apiAttempt == 0) ? "AAudio" : "OpenSL ES", 
                 oboe::convertToText(result));
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
        
        LOGI("Stream parameters - SampleRate: %d, BufferSize: %d, Channels: %d, Format: %s",
             mSampleRate.load(), mBufferSize.load(), mChannelCount.load(),
             (mAudioFormat.load() == oboe::AudioFormat::Float) ? "Float" : "I16");
    }
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized.load()) {
        LOGI("Already initialized");
        return true;
    }

    std::lock_guard<std::mutex> lock(mInitMutex);
    if (mIsInitialized.load()) {
        return true;
    }

    LOGI("Initializing Oboe audio renderer...");
    const int maxRetries = 3;
    
    for (int attempt = 0; attempt < maxRetries; attempt++) {
        if (attempt > 0) {
            LOGI("Retry attempt %d/%d", attempt, maxRetries);
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }

        // 先尝试Float格式
        if (openStreamWithFormat(oboe::AudioFormat::Float)) {
            LOGI("Successfully opened stream with Float format");
            break;
        }
        
        // 如果Float格式失败，尝试I16格式
        if (openStreamWithFormat(oboe::AudioFormat::I16)) {
            LOGI("Successfully opened stream with I16 format");
            break;
        }
        
        LOGE("Failed to open stream (attempt %d/%d)", attempt + 1, maxRetries);
    }

    if (!mAudioStream) {
        LOGE("All attempts to open Oboe stream failed");
        return false;
    }

    updateStreamParameters();
    mIsInitialized.store(true);
    mUnderrunCount = 0;
    mTotalFramesWritten = 0;
    
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
    
    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    
    LOGI("Oboe audio renderer initialized successfully");
    return true;
}

void OboeAudioRenderer::shutdown() {
    LOGI("Shutting down Oboe audio renderer");
    
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
    
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
    
    LOGI("Oboe audio renderer shutdown complete");
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    if (bufferSize < 64 || bufferSize > 8192) {
        LOGW("Invalid buffer size: %d", bufferSize);
        return;
    }
    mBufferSize.store(bufferSize);
    LOGI("Buffer size set to: %d", bufferSize);
}

void OboeAudioRenderer::setVolume(float volume) {
    float clampedVolume = std::clamp(volume, 0.0f, 1.0f);
    mVolume.store(clampedVolume);
    LOGI("Volume set to: %.2f", clampedVolume);
}

// 修复声道转换函数 - 使用标准下混算法
void OboeAudioRenderer::convertChannels(const float* input, float* output, int32_t numFrames, int32_t inputChannels, int32_t outputChannels) {
    if (inputChannels == outputChannels) {
        std::memcpy(output, input, numFrames * inputChannels * sizeof(float));
        return;
    }
    
    LOGI("Converting channels: %d -> %d, frames: %d", inputChannels, outputChannels, numFrames);
    
    // 标准下混算法
    if (inputChannels == 6 && outputChannels == 2) {
        // 6声道(5.1)转立体声的标准下混算法
        for (int i = 0; i < numFrames; i++) {
            int inIdx = i * 6;
            int outIdx = i * 2;
            
            // 左声道: 前左 + 0.707*中置 + 0.5*后左
            output[outIdx] = input[inIdx] + 0.707f * input[inIdx + 2] + 0.5f * input[inIdx + 4];
            
            // 右声道: 前右 + 0.707*中置 + 0.5*后右
            output[outIdx + 1] = input[inIdx + 1] + 0.707f * input[inIdx + 2] + 0.5f * input[inIdx + 5];
            
            // 限制幅度防止削波
            output[outIdx] = std::clamp(output[outIdx], -1.0f, 1.0f);
            output[outIdx + 1] = std::clamp(output[outIdx + 1], -1.0f, 1.0f);
        }
    } else if (inputChannels == 1 && outputChannels == 2) {
        // 单声道转立体声
        for (int i = 0; i < numFrames; i++) {
            output[i * 2] = input[i];
            output[i * 2 + 1] = input[i];
        }
    } else if (inputChannels == 2 && outputChannels == 1) {
        // 立体声转单声道
        for (int i = 0; i < numFrames; i++) {
            output[i] = (input[i * 2] + input[i * 2 + 1]) * 0.5f;
        }
    } else {
        // 通用下混算法
        int minChannels = std::min(inputChannels, outputChannels);
        for (int i = 0; i < numFrames; i++) {
            for (int j = 0; j < minChannels; j++) {
                output[i * outputChannels + j] = input[i * inputChannels + j];
            }
            for (int j = minChannels; j < outputChannels; j++) {
                output[i * outputChannels + j] = 0.0f;
            }
        }
        LOGW("Using generic channel conversion: %d -> %d", inputChannels, outputChannels);
    }
}

void OboeAudioRenderer::deinterleave(const float* interleaved, float** deinterleaved, int32_t numFrames, int32_t numChannels) {
    for (int ch = 0; ch < numChannels; ch++) {
        for (int i = 0; i < numFrames; i++) {
            deinterleaved[ch][i] = interleaved[i * numChannels + ch];
        }
    }
}

void OboeAudioRenderer::interleave(float** deinterleaved, float* interleaved, int32_t numFrames, int32_t numChannels) {
    for (int i = 0; i < numFrames; i++) {
        for (int ch = 0; ch < numChannels; ch++) {
            interleaved[i * numChannels + ch] = deinterleaved[ch][i];
        }
    }
}

// 核心修改：修复声道转换和采样率转换的顺序问题
void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames, int32_t inputChannels, int32_t inputSampleRate) {
    if (!mIsInitialized.load()) {
        if (!initialize()) {
            LOGE("Failed to initialize Oboe audio renderer");
            return;
        }
    }

    if (!data || numFrames <= 0 || inputChannels <= 0 || inputSampleRate <= 0) {
        LOGE("Invalid parameters: data=%p, frames=%d, channels=%d, sampleRate=%d", 
             data, numFrames, inputChannels, inputSampleRate);
        return;
    }

    if (numFrames > 48000 * 2) {
        LOGE("writeAudio: numFrames too large: %d", numFrames);
        return;
    }

    if (!mIsStreamStarted.load()) {
        std::lock_guard<std::mutex> lock(mInitMutex);
        if (!mIsStreamStarted.load()) {
            oboe::Result result = mAudioStream->requestStart();
            if (result == oboe::Result::OK) {
                mIsStreamStarted.store(true);
                LOGI("Oboe stream started successfully");
            } else {
                LOGE("Failed to start Oboe stream: %s", oboe::convertToText(result));
                return;
            }
        }
    }

    int32_t outputChannels = mChannelCount.load();
    int32_t deviceSampleRate = mSampleRate.load();

    LOGI("Processing audio: %d frames, %dch @ %dHz -> %dch @ %dHz", 
         numFrames, inputChannels, inputSampleRate, outputChannels, deviceSampleRate);

    // 1. 先进行声道转换
    std::vector<float> channelConvertedData;
    const float* dataToProcess = data;
    int32_t framesToProcess = numFrames;
    int32_t channelsAfterChannelConv = inputChannels;

    if (inputChannels != outputChannels) {
        try {
            channelConvertedData.resize(numFrames * outputChannels);
            convertChannels(data, channelConvertedData.data(), numFrames, inputChannels, outputChannels);
            dataToProcess = channelConvertedData.data();
            channelsAfterChannelConv = outputChannels;
        } catch (const std::exception& e) {
            LOGE("Channel conversion failed: %s", e.what());
            return;
        }
    }

    // 2. 再进行采样率转换（如果需要）
    if (inputSampleRate != deviceSampleRate) {
        LOGI("Sample rate conversion needed: %d -> %d", inputSampleRate, deviceSampleRate);
        try {
            // 计算输出帧数
            double ratio = static_cast<double>(deviceSampleRate) / inputSampleRate;
            size_t expectedOutputFrames = static_cast<size_t>(std::ceil(framesToProcess * ratio)) + 10;
            
            // 为转换后的数据分配缓冲区
            std::vector<float> sampleRateConvertedData(expectedOutputFrames * channelsAfterChannelConv);
            
            // 设置转换比率
            for (int ch = 0; ch < channelsAfterChannelConv; ch++) {
                mChannelConverters[ch]->setRatio(static_cast<float>(inputSampleRate), static_cast<float>(deviceSampleRate));
            }
            
            // 解交错
            std::vector<float*> deinterleavedInput(channelsAfterChannelConv);
            std::vector<std::vector<float>> inputChannelsData(channelsAfterChannelConv);
            
            for (int ch = 0; ch < channelsAfterChannelConv; ch++) {
                inputChannelsData[ch].resize(framesToProcess);
                deinterleavedInput[ch] = inputChannelsData[ch].data();
            }
            
            deinterleave(dataToProcess, deinterleavedInput.data(), framesToProcess, channelsAfterChannelConv);

            // 为每个通道转换采样率
            std::vector<float*> deinterleavedOutput(channelsAfterChannelConv);
            std::vector<std::vector<float>> outputChannelsData(channelsAfterChannelConv);
            std::vector<size_t> outputFramesPerChannel(channelsAfterChannelConv, 0);
            
            for (int ch = 0; ch < channelsAfterChannelConv; ch++) {
                outputChannelsData[ch].resize(expectedOutputFrames);
                deinterleavedOutput[ch] = outputChannelsData[ch].data();
                
                outputFramesPerChannel[ch] = mChannelConverters[ch]->convert(
                    inputChannelsData[ch].data(), framesToProcess, 
                    outputChannelsData[ch].data(), expectedOutputFrames);
            }
            
            // 找到最小的输出帧数
            size_t minOutputFrames = *std::min_element(outputFramesPerChannel.begin(), 
                                                     outputFramesPerChannel.end());
            
            if (minOutputFrames > 0) {
                // 重新交错
                interleave(deinterleavedOutput.data(), sampleRateConvertedData.data(), 
                          minOutputFrames, channelsAfterChannelConv);
                
                // 写入环形缓冲区
                size_t totalSamples = minOutputFrames * channelsAfterChannelConv;
                if (!mRingBuffer->write(sampleRateConvertedData.data(), totalSamples)) {
                    LOGW("RingBuffer write failed during sample rate conversion");
                }
                mTotalFramesWritten += minOutputFrames;
                
                LOGI("Sample rate conversion complete: %d -> %d frames", framesToProcess, minOutputFrames);
            } else {
                LOGW("Sample rate conversion produced no output frames");
            }
        } catch (const std::exception& e) {
            LOGE("Sample rate conversion failed: %s", e.what());
            return;
        }
    } else {
        // 不需要采样率转换，直接写入
        size_t totalSamples = framesToProcess * channelsAfterChannelConv;
        if (!mRingBuffer->write(dataToProcess, totalSamples)) {
            LOGW("RingBuffer write failed for direct data");
        }
        mTotalFramesWritten += framesToProcess;
    }
}

void OboeAudioRenderer::clearBuffer() {
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
    
    LOGI("Audio buffer cleared");
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
    size_t bufferedSamples = mRingBuffer->available();
    size_t bufferedFrames = bufferedSamples / channelCount;

    LOGI("onAudioReady: requested %d frames, buffered %zu frames", numFrames, bufferedFrames);

    if (bufferedSamples < totalSamples) {
        mUnderrunCount++;
        LOGW("Buffer underrun: available=%zu, needed=%zu (underrun count=%zu)", 
             bufferedSamples, totalSamples, mUnderrunCount.load());
             
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

        if (volume != 1.0f) {
            for (size_t i = 0; i < totalSamples; i++) {
                output[i] *= volume;
            }
        }
    }

    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("onErrorAfterClose: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    if (mNoiseShaper) mNoiseShaper->reset();
    
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    LOGE("onErrorBeforeClose: %s", oboe::convertToText(error));
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    if (mNoiseShaper) mNoiseShaper->reset();
    
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
}
