#include "stabilized_audio_callback.h"
#include <algorithm>
#include <atomic>

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
    
    // 计算时间戳（用于性能监控）
    if (mEpochTimeNanos == 0) {
        mEpochTimeNanos = getCurrentTimeNanos();
    }
    
    // 执行实际的音频回调
    auto result = mCallback->onAudioReady(oboeStream, audioData, numFrames);
    
    // 生成可控负载以稳定CPU频率
    if (mLoadIntensity.load() > 0.01f) {
        // 根据负载强度计算持续时间（微秒）
        int64_t loadDurationMicros = static_cast<int64_t>(50.0f * mLoadIntensity.load());
        generateLoad(loadDurationMicros * 1000); // 转换为纳秒
    }
    
    mFrameCount += numFrames;
    return result;
}

void StabilizedAudioCallback::generateLoad(int64_t durationNanos) {
    if (durationNanos <= 0) return;
    
    int64_t startTime = getCurrentTimeNanos();
    int64_t currentTime = startTime;
    int64_t targetTime = startTime + durationNanos;
    
    // 简单的负载生成循环
    int iterations = 0;
    while (currentTime < targetTime) {
        // 执行一些计算工作
        for (int i = 0; i < 100; ++i) {
            double value = std::sin(static_cast<double>(iterations + i));
            value = std::cos(value * 3.14159);
            (void)value; // 避免编译器优化掉
        }
        
        iterations++;
        cpuRelax();
        currentTime = getCurrentTimeNanos();
    }
}

void StabilizedAudioCallback::cpuRelax() {
#if defined(__i386__) || defined(__x86_64__)
    asm volatile("rep; nop" ::: "memory");
#elif defined(__arm__) || defined(__mips__) || defined(__riscv)
    asm volatile("" ::: "memory");
#elif defined(__aarch64__)
    asm volatile("yield" ::: "memory");
#else
    // 通用回退：简单的内存屏障
    std::atomic_signal_fence(std::memory_order_acq_rel);
#endif
}

} // namespace RyujinxOboe