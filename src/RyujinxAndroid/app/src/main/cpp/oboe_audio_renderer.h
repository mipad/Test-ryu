// oboe_audio_renderer.h
#ifndef RYUJINX_OBOE_AUDIO_RENDERER_H
#define RYUJINX_OBOE_AUDIO_RENDERER_H

#include <oboe/Oboe.h>
#include <mutex>
#include <vector>
#include <atomic>

class OboeAudioRenderer : public oboe::AudioStreamDataCallback {
public:
    static OboeAudioRenderer& getInstance();
    
    bool initialize();
    void shutdown();
    
    void setSampleRate(int32_t sampleRate);
    void setBufferSize(int32_t bufferSize);
    
    void writeAudio(const float* data, int32_t numFrames);
    void clearBuffer();
    
    // oboe::AudioStreamDataCallback interface
    oboe::DataCallbackResult onAudioReady(oboe::AudioStream* audioStream, void* audioData, int32_t numFrames) override;
    void onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) override;
    
private:
    OboeAudioRenderer();
    ~OboeAudioRenderer();
    
    std::unique_ptr<oboe::AudioStream> mAudioStream;
    std::vector<float> mAudioBuffer;
    std::mutex mBufferMutex;
    std::atomic<bool> mIsInitialized{false};
    std::atomic<int32_t> mSampleRate{48000};
    std::atomic<int32_t> mBufferSize{1024};
};

#endif // RYUJINX_OBOE_AUDIO_RENDERER_H
