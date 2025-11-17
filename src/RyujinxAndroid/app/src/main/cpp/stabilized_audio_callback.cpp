#include "stabilized_audio_callback.h"
#include <algorithm>
#include <atomic>
#include <android/log.h>

namespace RyujinxOboe {

StabilizedAudioCallback::StabilizedAudioCallback(
    std::shared_ptr<oboe::AudioStreamDataCallback> dataCallback,
    std::shared_ptr<oboe::AudioStreamErrorCallback> errorCallback) 
    : mDataCallback(dataCallback), mErrorCallback(errorCallback) {
}

oboe::DataCallbackResult StabilizedAudioCallback::onAudioReady(
    oboe::AudioStream *oboeStream, void *audioData, int32_t numFrames) {
    
    // 检查流状态
    if (!oboeStream || oboeStream->getState() != oboe::StreamState::Started) {
        return oboe::DataCallbackResult::Stop;
    }
    
    // 如果不启用稳定回调，直接传递调用
    if (!mEnabled.load() || !mDataCallback) {
        return mDataCallback->onAudioReady(oboeStream, audioData, numFrames);
    }
    
    // 计算时间戳（用于性能监控）
    if (mEpochTimeNanos == 0) {
        mEpochTimeNanos = getCurrentTimeNanos();
    }
    
    // 执行实际的音频回调
    auto result = mDataCallback->onAudioReady(oboeStream, audioData, numFrames);
    
    // 大幅降低负载生成频率和强度
    if (result == oboe::DataCallbackResult::Continue && 
        mLoadIntensity.load() > 0.01f &&
        (mFrameCount % 4 == 0)) {  // 每4次回调执行一次负载生成
        // 大幅降低负载强度
        int64_t loadDurationMicros = static_cast<int64_t>(10.0f * mLoadIntensity.load());
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
    
    // 大幅简化的负载生成 - 只做必要的最小计算
    volatile int dummyValue = 0;  // 使用volatile防止优化
    while (currentTime < targetTime) {
        // 极简的计算工作 - 只做1次简单计算而不是100次复杂计算
        dummyValue += 1;
        
        // 更频繁地检查时间，减少不必要的计算
        cpuRelax();
        currentTime = getCurrentTimeNanos();
    }
    
    // 防止编译器优化掉dummyValue
    if (dummyValue == 0) {
        __android_log_print(ANDROID_LOG_VERBOSE, "StabilizedAudioCallback", 
                           "Dummy value: %d", dummyValue);
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