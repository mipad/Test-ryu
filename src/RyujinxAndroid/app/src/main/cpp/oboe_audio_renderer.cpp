// oboe_audio_renderer.cpp
#include "oboe_audio_renderer.h"
#include <android/log.h>
#include <cstring>

// 定义 Android 日志宏
#ifndef ALOGE
#define ALOGE(...) __android_log_print(ANDROID_LOG_ERROR, "OboeAudio", __VA_ARGS__)
#endif

#ifndef ALOGI
#define ALOGI(...) __android_log_print(ANDROID_LOG_INFO, "OboeAudio", __VA_ARGS__)
#endif

OboeAudioRenderer::OboeAudioRenderer() {
    // 初始化音频缓冲区
    mAudioBuffer.reserve(mBufferSize * 2 * 10); // 10倍缓冲区大小
}

OboeAudioRenderer::~OboeAudioRenderer() {
    shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::getInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::initialize() {
    if (mIsInitialized) {
        return true;
    }
    
    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output);
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency);
    builder.setSharingMode(oboe::SharingMode::Exclusive);
    builder.setFormat(oboe::AudioFormat::Float);
    builder.setChannelCount(oboe::ChannelCount::Stereo);
    builder.setSampleRate(mSampleRate);
    builder.setBufferCapacityInFrames(mBufferSize * 2);
    builder.setDataCallback(this);  // 设置数据回调
    builder.setErrorCallback(this); // 设置错误回调
    
    // 创建 shared_ptr 并传递给 openStream
    std::shared_ptr<oboe::AudioStream> stream;
    oboe::Result result = builder.openStream(stream);
    if (result != oboe::Result::OK) {
        ALOGE("Failed to open Oboe stream: %s", oboe::convertToText(result));
        return false;
    }
    
    mAudioStream = stream; // 赋值给成员变量
    
    result = mAudioStream->requestStart();
    if (result != oboe::Result::OK) {
        ALOGE("Failed to start Oboe stream: %s", oboe::convertToText(result));
        mAudioStream->close();
        mAudioStream.reset();
        return false;
    }
    
    mIsInitialized = true;
    ALOGI("Oboe audio stream initialized with sample rate: %d, buffer size: %d", 
          mAudioStream->getSampleRate(), mAudioStream->getBufferSizeInFrames());
    
    return true;
}

void OboeAudioRenderer::shutdown() {
    if (mAudioStream) {
        mAudioStream->stop();
        mAudioStream->close();
        mAudioStream.reset();
    }
    
    clearBuffer();
    mIsInitialized = false;
}

void OboeAudioRenderer::setSampleRate(int32_t sampleRate) {
    mSampleRate = sampleRate;
}

void OboeAudioRenderer::setBufferSize(int32_t bufferSize) {
    mBufferSize = bufferSize;
}

void OboeAudioRenderer::writeAudio(const float* data, int32_t numFrames) {
    if (!mIsInitialized) {
        return;
    }
    
    std::lock_guard<std::mutex> lock(mBufferMutex);
    size_t currentSize = mAudioBuffer.size();
    mAudioBuffer.resize(currentSize + numFrames * 2); // 立体声
    
    std::memcpy(mAudioBuffer.data() + currentSize, data, numFrames * 2 * sizeof(float));
}

void OboeAudioRenderer::clearBuffer() {
    std::lock_guard<std::mutex> lock(mBufferMutex);
    mAudioBuffer.clear();
}

oboe::DataCallbackResult OboeAudioRenderer::onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) {
    if (!mIsInitialized) {
        return oboe::DataCallbackResult::Stop;
    }
    
    float* output = static_cast<float*>(audioData);
    int32_t framesToWrite = numFrames;
    
    std::lock_guard<std::mutex> lock(mBufferMutex);
    
    if (mAudioBuffer.size() < framesToWrite * 2) {
        // 没有足够的数据，用静音填充
        std::memset(output, 0, framesToWrite * 2 * sizeof(float));
        return oboe::DataCallbackResult::Continue;
    }
    
    // 复制数据到输出缓冲区
    std::memcpy(output, mAudioBuffer.data(), framesToWrite * 2 * sizeof(float));
    
    // 移除已处理的数据
    mAudioBuffer.erase(mAudioBuffer.begin(), mAudioBuffer.begin() + framesToWrite * 2);
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    ALOGE("Oboe audio stream error: %s", oboe::convertToText(error));
    mIsInitialized = false;
}
