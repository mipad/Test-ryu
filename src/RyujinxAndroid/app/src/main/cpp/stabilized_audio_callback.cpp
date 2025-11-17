#include "stabilized_audio_callback.h"
#include <chrono>
#include <cmath>

namespace RyujinxOboe {

StabilizedAudioCallback::StabilizedAudioCallback(oboe::AudioStreamCallback *callback) 
    : mCallback(callback) {
}

oboe::DataCallbackResult StabilizedAudioCallback::onAudioReady(
    oboe::AudioStream *oboeStream, void *audioData, int32_t numFrames) {
    
    // 如果不启用稳定回调，直接传递调用
    if (!mEnabled.load() || !mCallback) {
        return mCallback->onAudioReady(oboeStream, audioData, numFrames);
    }
    
    // 执行实际的音频回调
    auto result = mCallback->onAudioReady(oboeStream, audioData, numFrames);
    
    // 生成可控负载以稳定CPU频率
    if (mLoadIntensity.load() > 0.01f) {
        // 根据负载强度计算持续时间
        int64_t loadDurationMicros = static_cast<int64_t>(40.0f * mLoadIntensity.load());
        generateLoad(loadDurationMicros * 1000); // 转换为纳秒
    }
    
    mFrameCount += numFrames;
    return result;
}

void StabilizedAudioCallback::generateLoad(int64_t durationNanos) {
    if (durationNanos <= 1000) return;
    
    int64_t startTime = getCurrentTimeNanos();
    int64_t currentTime = startTime;
    int64_t targetTime = startTime + durationNanos;
    
    // 简单的负载生成循环
    int iterations = 0;
    while (currentTime < targetTime && iterations < 1000) {
        // 执行一些数学运算
        volatile double value = 0.0;
        for (int i = 0; i < 50; ++i) {
            value += std::sin(static_cast<double>(i));
            value += std::cos(value);
        }
        
        iterations++;
        currentTime = getCurrentTimeNanos();
    }
}

int64_t StabilizedAudioCallback::getCurrentTimeNanos() {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

} // namespace RyujinxOboe