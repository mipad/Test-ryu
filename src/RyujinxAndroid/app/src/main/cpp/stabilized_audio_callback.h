#ifndef RYUJINX_STABILIZED_AUDIO_CALLBACK_H
#define RYUJINX_STABILIZED_AUDIO_CALLBACK_H

#include <oboe/Oboe.h>
#include <cstdint>
#include <chrono>
#include <atomic>
#include <cmath>
#include <memory>

namespace RyujinxOboe {

class StabilizedAudioCallback : public oboe::AudioStreamCallback {
public:
    // 构造函数接受数据回调和错误回调
    StabilizedAudioCallback(std::shared_ptr<oboe::AudioStreamDataCallback> dataCallback,
                          std::shared_ptr<oboe::AudioStreamErrorCallback> errorCallback);
    ~StabilizedAudioCallback() override = default;
    
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream *oboeStream, 
                                         void *audioData, 
                                         int32_t numFrames) override;
    
    void onErrorBeforeClose(oboe::AudioStream *oboeStream, oboe::Result error) override {
        if (mErrorCallback) {
            mErrorCallback->onErrorBeforeClose(oboeStream, error);
        }
    }
    
    void onErrorAfterClose(oboe::AudioStream *oboeStream, oboe::Result error) override {
        // Reset state
        mFrameCount = 0;
        mEpochTimeNanos = 0;
        mOpsPerNano = 1.0;
        if (mErrorCallback) {
            mErrorCallback->onErrorAfterClose(oboeStream, error);
        }
    }

    bool onError(oboe::AudioStream* audioStream, oboe::Result error) override {
        if (mErrorCallback) {
            return mErrorCallback->onError(audioStream, error);
        }
        return false;
    }

    // 启用/禁用稳定回调
    void setEnabled(bool enabled) { mEnabled.store(enabled); }
    bool isEnabled() const { return mEnabled.load(); }

    // 设置负载强度 (0.0 - 1.0)
    void setLoadIntensity(float intensity) { 
        mLoadIntensity.store(std::max(0.0f, std::min(intensity, 1.0f))); 
    }
    float getLoadIntensity() const { return mLoadIntensity.load(); }

private:
    std::shared_ptr<oboe::AudioStreamDataCallback> mDataCallback;
    std::shared_ptr<oboe::AudioStreamErrorCallback> mErrorCallback;
    int64_t mFrameCount = 0;
    int64_t mEpochTimeNanos = 0;
    double mOpsPerNano = 1.0;
    std::atomic<bool> mEnabled{true};
    std::atomic<float> mLoadIntensity{0.3f}; // 默认中等负载
    
    void generateLoad(int64_t durationNanos);
    int64_t getCurrentTimeNanos() {
        return std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    // CPU放松指令，针对不同架构优化
    static void cpuRelax();
};

} // namespace RyujinxOboe

#endif // RYUJINX_STABILIZED_AUDIO_CALLBACK_H