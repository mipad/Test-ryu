// oboe_audio_renderer.cpp (终极修复版：支持多通道分别采样率转换)
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
#define LOGD(...) 
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
    if (outputRate <= 0) {
        LOGE("Invalid output rate: %f", outputRate);
        return;
    }
    mRatio = inputRate / outputRate;
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

    // 添加历史误差
    float shaped = input + mHistory[0];
    shaped = std::clamp(shaped, -1.0f, 1.0f); // 防止溢出

    // 计算量化误差（量化在外部进行）
    float quantized = std::round(shaped * 32767.0f) / 32767.0f;
    float error = shaped - quantized;

    // 一阶反馈更新历史
    mHistory[2] = mHistory[1];
    mHistory[1] = mHistory[0];
    mHistory[0] = error * 0.95f; // 保守反馈系数，避免不稳定

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
    : mRingBuffer(std::make_unique<RingBuffer>((48000 * 2 * 250) / 1000)), // 250ms缓冲
      mLastBufferLevel(0),
      mUnderrunCount(0),
      mTotalFramesWritten(0)
{
    // 为每个通道创建采样率转换器
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i] = std::make_unique<SampleRateConverter>();
    }
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
}

// 设置的是 Oboe 输出流的声道数
void OboeAudioRenderer::setChannelCount(int32_t channelCount) {
    mChannelCount.store(channelCount);
    // 只有当采样率不同时才更新转换器比率
    if (mSampleRate.load() != 48000) {
        for (int i = 0; i < channelCount && i < MAX_CHANNELS; i++) {
            mChannelConverters[i]->setRatio(48000.0f, static_cast<float>(mSampleRate.load()));
        }
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
               ->setChannelCount(mChannelCount.load()) // 使用设置的输出声道数
               ->setSampleRate(mSampleRate.load())
               ->setBufferCapacityInFrames(oboe::DefaultStreamValues::FramesPerBurst * 4) // 增大缓冲
               ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::High)
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
        mChannelCount.store(mAudioStream->getChannelCount()); // 更新为设备实际打开的声道数
        mAudioFormat.store(mAudioStream->getFormat());

        // 只有当采样率不同时才更新转换器比率
        if (mSampleRate.load() != 48000) {
            for (int i = 0; i < mChannelCount && i < MAX_CHANNELS; i++) {
                mChannelConverters[i]->setRatio(48000.0f, static_cast<float>(mSampleRate.load()));
            }
        }
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

        // 恢复为先尝试Float格式
        if (!openStreamWithFormat(oboe::AudioFormat::Float)) {
            if (!openStreamWithFormat(oboe::AudioFormat::I16)) {
                continue;
            }
        }

        updateStreamParameters();
        mIsInitialized.store(true);
        mUnderrunCount = 0;
        mTotalFramesWritten = 0;
        
        // 重置所有通道的采样率转换器
        for (int i = 0; i < MAX_CHANNELS; i++) {
            mChannelConverters[i]->reset();
        }
        
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
    mUnderrunCount = 0;
    mTotalFramesWritten = 0;

    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    
    // 重置所有通道转换器
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    if (sampleRate < 8000 || sampleRate > 192000) {
        return;
    }
    mSampleRate.store(sampleRate);
    
    // 只有当采样率不同时才更新转换器比率
    if (sampleRate != 48000) {
        for (int i = 0; i < mChannelCount && i < MAX_CHANNELS; i++) {
            mChannelConverters[i]->setRatio(48000.0f, static_cast<float>(sampleRate));
        }
    }
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

void OboeAudioRenderer::convertChannels(const float* input, float* output, int32_t numFrames, int32_t inputChannels, int32_t outputChannels) {
    if (inputChannels == outputChannels) {
        // 通道数相同，直接复制
        std::memcpy(output, input, numFrames * inputChannels * sizeof(float));
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
    } else if (inputChannels == 6 && outputChannels == 2) {
        // 5.1 转立体声
        for (int i = 0; i < numFrames; i++) {
            int inIdx = i * 6;
            int outIdx = i * 2;
            // 左 = FL + 0.5*C + 0.7*SL
            output[outIdx] = input[inIdx] + input[inIdx + 2] * 0.5f + input[inIdx + 4] * 0.7f;
            // 右 = FR + 0.5*C + 0.7*SR
            output[outIdx + 1] = input[inIdx + 1] + input[inIdx + 2] * 0.5f + input[inIdx + 5] * 0.7f;
        }
    } else {
        // 其他通道转换，简单处理：取前N个声道或填充0
        int minChannels = std::min(inputChannels, outputChannels);
        for (int i = 0; i < numFrames; i++) {
            for (int j = 0; j < minChannels; j++) {
                output[i * outputChannels + j] = input[i * inputChannels + j];
            }
            // 多余的通道填充0
            for (int j = minChannels; j < outputChannels; j++) {
                output[i * outputChannels + j] = 0.0f;
            }
        }
        LOGW("Unoptimized channel conversion: %d -> %d", inputChannels, outputChannels);
    }
}

// 辅助函数：解交错
void OboeAudioRenderer::deinterleave(const float* interleaved, float** deinterleaved, int32_t numFrames, int32_t numChannels) {
    for (int ch = 0; ch < numChannels; ch++) {
        for (int i = 0; i < numFrames; i++) {
            deinterleaved[ch][i] = interleaved[i * numChannels + ch];
        }
    }
}

// 辅助函数：重新交错
void OboeAudioRenderer::interleave(float** deinterleaved, float* interleaved, int32_t numFrames, int32_t numChannels) {
    for (int i = 0; i < numFrames; i++) {
        for (int ch = 0; ch < numChannels; ch++) {
            interleaved[i * numChannels + ch] = deinterleaved[ch][i];
        }
    }
}

// 核心修改：处理任意输入，统一在 C++ 端进行转换
void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames, int32_t inputChannels) {
    if (!mIsInitialized.load()) {
        if (!initialize()) {
            return;
        }
    }

    if (!data || numFrames <= 0 || inputChannels <= 0) {
        return;
    }

    // 添加安全检查，防止过大帧数导致内存分配失败
    if (numFrames > 48000 * 2) { // 最大允许2秒的帧数（48000Hz）
        LOGE("writeAudio: numFrames too large: %d", numFrames);
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

    int32_t outputChannels = mChannelCount.load(); // 获取 Oboe 输出流的声道数

    // 1. 声道数转换 (如果需要)
    std::vector<float> channelConvertedData;
    const float* dataToProcess = data;
    int32_t framesToProcess = numFrames;
    int32_t channelsAfterChannelConv = inputChannels;

    if (inputChannels != outputChannels) {
        LOGI("Performing channel conversion: %d -> %d", inputChannels, outputChannels);
        try {
            channelConvertedData.resize(numFrames * outputChannels);
            convertChannels(data, channelConvertedData.data(), numFrames, inputChannels, outputChannels);
            dataToProcess = channelConvertedData.data();
            channelsAfterChannelConv = outputChannels;
            // framesToProcess 保持不变
        } catch (const std::exception& e) {
            LOGE("Channel conversion failed: %s", e.what());
            return;
        }
    }

    // 2. 动态采样率转换 - 只在采样率不同时进行
    if (mSampleRate.load() != 48000) {
        LOGI("Performing sample rate conversion: 48000 -> %d", mSampleRate.load());
        try {
            // 2.1 解交错：分离各通道数据
            std::vector<float*> deinterleavedInput(channelsAfterChannelConv);
            std::vector<std::vector<float>> inputChannelsData(channelsAfterChannelConv);
            
            for (int ch = 0; ch < channelsAfterChannelConv; ch++) {
                inputChannelsData[ch].resize(framesToProcess);
                deinterleavedInput[ch] = inputChannelsData[ch].data();
            }
            
            deinterleave(dataToProcess, deinterleavedInput.data(), framesToProcess, channelsAfterChannelConv);

            // 2.2 计算输出帧数 (使用最大可能值)
            size_t maxOutputFrames = static_cast<size_t>(
                std::ceil(framesToProcess * (static_cast<float>(mSampleRate.load()) / 48000.0f))) + 10;
            
            // 2.3 为每个通道分配输出缓冲区
            std::vector<float*> deinterleavedOutput(channelsAfterChannelConv);
            std::vector<std::vector<float>> outputChannelsData(channelsAfterChannelConv);
            std::vector<size_t> outputFramesPerChannel(channelsAfterChannelConv, 0);
            
            for (int ch = 0; ch < channelsAfterChannelConv; ch++) {
                outputChannelsData[ch].resize(maxOutputFrames);
                deinterleavedOutput[ch] = outputChannelsData[ch].data();
                
                // 2.4 对每个通道单独进行采样率转换
                outputFramesPerChannel[ch] = mChannelConverters[ch]->convert(
                    inputChannelsData[ch].data(), framesToProcess, 
                    outputChannelsData[ch].data(), maxOutputFrames);
            }
            
            // 2.5 找到最小的输出帧数 (确保所有通道长度一致)
            size_t minOutputFrames = *std::min_element(outputFramesPerChannel.begin(), 
                                                     outputFramesPerChannel.end());
            
            if (minOutputFrames > 0) {
                // 2.6 重新交错各通道数据
                std::vector<float> finalOutput(minOutputFrames * channelsAfterChannelConv);
                interleave(deinterleavedOutput.data(), finalOutput.data(), 
                          minOutputFrames, channelsAfterChannelConv);
                
                // 2.7 写入环形缓冲区
                if (!mRingBuffer->write(finalOutput.data(), finalOutput.size())) {
                    LOGW("RingBuffer write failed during sample rate conversion");
                }
                mTotalFramesWritten += minOutputFrames; // 更新写入的帧数
            }
        } catch (const std::exception& e) {
            LOGE("Per-channel sample rate conversion failed: %s", e.what());
            return;
        }
    } else {
        // 3. 不需要采样率转换，直接写入
        LOGI("No sample rate conversion needed, writing directly to ring buffer");
        size_t totalSamples = framesToProcess * channelsAfterChannelConv;
        if (!mRingBuffer->write(dataToProcess, totalSamples)) {
            LOGW("RingBuffer write failed for direct data");
        }
        mTotalFramesWritten += framesToProcess; // 更新写入的帧数
    }
}

void OboeAudioRenderer::clearBuffer() {
    if (mRingBuffer) {
        mRingBuffer->clear();
    }
    if (mNoiseShaper) {
        mNoiseShaper->reset();
    }
    
    // 清除所有通道转换器的状态
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
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

    // 欠载保护
    if (bufferedSamples < totalSamples) {
        mUnderrunCount++;
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

        // 应用音量 + 噪声整形（仅I16，且默认关闭）
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
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    if (mNoiseShaper) mNoiseShaper->reset();
    
    // 重置所有通道转换器
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
}

void OboeAudioRenderer::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    mIsStreamStarted.store(false);
    mIsInitialized.store(false);
    if (mNoiseShaper) mNoiseShaper->reset();
    
    // 重置所有通道转换器
    for (int i = 0; i < MAX_CHANNELS; i++) {
        mChannelConverters[i]->reset();
    }
}
