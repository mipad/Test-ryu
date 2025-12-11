#include "stabilized_audio_callback.h"
#include <algorithm>
#include <atomic>

namespace RyujinxOboe {

StabilizedAudioCallback::StabilizedAudioCallback(
    std::shared_ptr<oboe::AudioStreamDataCallback> dataCallback,
    std::shared_ptr<oboe::AudioStreamErrorCallback> errorCallback) 
    : mDataCallback(dataCallback), mErrorCallback(errorCallback) {
}

oboe::DataCallbackResult StabilizedAudioCallback::onAudioReady(
    oboe::AudioStream *oboeStream, void *audioData, int32_t numFrames) {
    
    if (!mEnabled.load() || !mDataCallback) {
        return mDataCallback->onAudioReady(oboeStream, audioData, numFrames);
    }
    
    if (mEpochTimeNanos == 0) {
        mEpochTimeNanos = getCurrentTimeNanos();
    }
    
    auto result = mDataCallback->onAudioReady(oboeStream, audioData, numFrames);
    
    if (mLoadIntensity.load() > 0.01f) {
        int64_t loadDurationMicros = static_cast<int64_t>(50.0f * mLoadIntensity.load());
        generateLoad(loadDurationMicros * 1000);
    }
    
    mFrameCount += numFrames;
    return result;
}

void StabilizedAudioCallback::generateLoad(int64_t durationNanos) {
    if (durationNanos <= 0) return;
    
    int64_t startTime = getCurrentTimeNanos();
    int64_t currentTime = startTime;
    int64_t targetTime = startTime + durationNanos;
    
    int iterations = 0;
    while (currentTime < targetTime) {
        for (int i = 0; i < 100; ++i) {
            double value = std::sin(static_cast<double>(iterations + i));
            value = std::cos(value * 3.14159);
            (void)value;
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
    std::atomic_signal_fence(std::memory_order_acq_rel);
#endif
}

} // namespace RyujinxOboe