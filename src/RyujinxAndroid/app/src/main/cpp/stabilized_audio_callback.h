#ifndef RYUJINX_STABILIZED_AUDIO_CALLBACK_H
#define RYUJINX_STABILIZED_AUDIO_CALLBACK_H

#include <oboe/Oboe.h>
#include <cstdint>
#include <chrono>
#include <atomic>
#include <cmath>

namespace RyujinxOboe {

class StabilizedAudioCallback : public oboe::AudioStreamCallback {
public:
    explicit StabilizedAudioCallback(oboe::AudioStreamCallback *callback);
    virtual ~StabilizedAudioCallback() = default;
    
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream *oboeStream, 
                                         void *audioData, 
                                         int32_t numFrames) override;
    
    void onErrorBeforeClose(oboe::AudioStream *oboeStream, oboe::Result error) override {
        if (mCallback) {
            mCallback->onErrorBeforeClose(oboeStream, error);
        }
    }
    
    void onErrorAfterClose(oboe::AudioStream *oboeStream, oboe::Result error) override {
        // Reset state for ARM optimization
        mFrameCount = 0;
        mEpochTimeNanos = 0;
        mOpsPerNano = 1.0;
        mLastLoadDuration = 0;
        if (mCallback) {
            mCallback->onErrorAfterClose(oboeStream, error);
        }
    }

    bool onError(oboe::AudioStream* audioStream, oboe::Result error) override {
        if (mCallback) {
            return mCallback->onError(audioStream, error);
        }
        return false;
    }

    // 启用/禁用稳定回调
    void setEnabled(bool enabled) { 
        mEnabled.store(enabled); 
        if (!enabled) {
            // 禁用时重置状态
            mFrameCount = 0;
            mEpochTimeNanos = 0;
            mLastLoadDuration = 0;
        }
    }
    bool isEnabled() const { return mEnabled.load(); }

    // 设置负载强度 (0.0 - 1.0) - ARM 优化范围
    void setLoadIntensity(float intensity) { 
        mLoadIntensity.store(std::max(0.0f, std::min(intensity, 1.0f))); 
    }
    float getLoadIntensity() const { return mLoadIntensity.load(); }

    // 获取性能统计
    int64_t getLastLoadDuration() const { return mLastLoadDuration; }
    int64_t getTotalFramesProcessed() const { return mFrameCount; }

private:
    oboe::AudioStreamCallback *mCallback = nullptr;
    int64_t mFrameCount = 0;
    int64_t mEpochTimeNanos = 0;
    double mOpsPerNano = 1.0;
    int64_t mLastLoadDuration = 0;
    std::atomic<bool> mEnabled{true};
    std::atomic<float> mLoadIntensity{0.3f}; // ARM 设备默认中等偏低负载
    
    // ARM 优化的负载生成
    void generateArmOptimizedLoad(int64_t durationNanos);
    
    // ARM 特定的数学计算负载
    void performArmMathWorkload(int iterations);
    
    int64_t getCurrentTimeNanos() {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    // ARM 优化的 CPU 放松指令
    static inline void armCpuRelax() {
#if defined(__arm__)
        asm volatile("" ::: "memory");
#elif defined(__aarch64__)
        asm volatile("yield" ::: "memory");
#else
        // 通用 ARM 回退
        asm volatile("" ::: "memory");
#endif
    }
};

} // namespace RyujinxOboe

#endif // RYUJINX_STABILIZED_AUDIO_CALLBACK_H