#include "stabilized_audio_callback.h"
#include <algorithm>
#include <array>

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
    
    // ARM 优化：初始化时间戳
    if (mEpochTimeNanos == 0) {
        mEpochTimeNanos = getCurrentTimeNanos();
    }
    
    // 执行实际的音频回调
    auto startCallbackTime = getCurrentTimeNanos();
    auto result = mCallback->onAudioReady(oboeStream, audioData, numFrames);
    auto callbackDuration = getCurrentTimeNanos() - startCallbackTime;
    
    // ARM 优化：根据回调执行时间动态调整负载
    if (mLoadIntensity.load() > 0.01f) {
        // 计算目标负载持续时间（微秒）
        // ARM 设备上使用更保守的负载设置
        const int64_t baseLoadMicros = 40; // 比 x86 更保守的基础负载
        int64_t targetLoadNanos = static_cast<int64_t>(baseLoadMicros * mLoadIntensity.load() * 1000);
        
        // 确保负载时间合理
        targetLoadNanos = std::max(static_cast<int64_t>(1000), std::min(targetLoadNanos, static_cast<int64_t>(100000)));
        
        generateArmOptimizedLoad(targetLoadNanos);
    }
    
    mFrameCount += numFrames;
    return result;
}

void StabilizedAudioCallback::generateArmOptimizedLoad(int64_t durationNanos) {
    if (durationNanos <= 1000) return; // 太短的持续时间跳过
    
    int64_t startTime = getCurrentTimeNanos();
    int64_t currentTime = startTime;
    int64_t targetTime = startTime + durationNanos;
    
    // ARM 优化：使用更适合 ARM 的负载模式
    int workloadIterations = 0;
    const int baseIterations = 50 + static_cast<int>(100 * mLoadIntensity.load());
    
    while (currentTime < targetTime && workloadIterations < 1000) {
        // 执行 ARM 优化的数学工作负载
        performArmMathWorkload(baseIterations);
        
        workloadIterations++;
        armCpuRelax();
        currentTime = getCurrentTimeNanos();
    }
    
    mLastLoadDuration = currentTime - startTime;
}

void StabilizedAudioCallback::performArmMathWorkload(int iterations) {
    // ARM 优化：使用更适合 ARM NEON 的数学运算
    volatile float accumulator = 0.0f;
    const float pi = 3.14159265358979323846f;
    
    for (int i = 0; i < iterations; ++i) {
        // 使用适合 ARM 的浮点运算
        float angle = static_cast<float>(i) * pi / 180.0f;
        
        // 正弦和余弦计算 - ARM 有硬件加速
        float sin_val = std::sin(angle);
        float cos_val = std::cos(angle);
        
        // 一些 ARM 友好的运算
        accumulator += sin_val * sin_val + cos_val * cos_val;
        accumulator = std::fmod(accumulator, 2.0f * pi);
        
        // 平方根 - ARM 有硬件加速
        if (i % 5 == 0) {
            accumulator = std::sqrt(std::abs(accumulator) + 1.0f);
        }
    }
    
    // 防止编译器优化掉整个循环
    if (accumulator > 1000000.0f) {
        // 这永远不会发生，但防止编译器优化
        asm volatile("" ::"r"(accumulator));
    }
}

} // namespace RyujinxOboe