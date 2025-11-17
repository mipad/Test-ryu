#ifndef RYUJINX_STABILIZED_AUDIO_CALLBACK_H
#define RYUJINX_STABILIZED_AUDIO_CALLBACK_H

#include <oboe/Oboe.h>
#include <cstdint>
#include <atomic>

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
        // Reset state
        mFrameCount = 0;
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
    }
    bool isEnabled() const { return mEnabled.load(); }

    // 设置负载强度 (0.0 - 1.0)
    void setLoadIntensity(float intensity) { 
        float clamped = intensity < 0.0f ? 0.0f : (intensity > 1.0f ? 1.0f : intensity);
        mLoadIntensity.store(clamped); 
    }
    float getLoadIntensity() const { return mLoadIntensity.load(); }

private:
    oboe::AudioStreamCallback *mCallback = nullptr;
    int64_t mFrameCount = 0;
    std::atomic<bool> mEnabled{false}; // 默认禁用
    std::atomic<float> mLoadIntensity{0.3f};
    
    // 简化的负载生成
    void generateLoad(int64_t durationNanos);
    
    int64_t getCurrentTimeNanos();
};

} // namespace RyujinxOboe

#endif // RYUJINX_STABILIZED_AUDIO_CALLBACK_H