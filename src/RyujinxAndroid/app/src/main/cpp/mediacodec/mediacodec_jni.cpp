#include <jni.h>
#include "mediacodec_common.h"
#include "mediacodec_decoder.h"
#include "mediacodec_h264.cpp"
#include "mediacodec_vp8.cpp"
#include "mediacodec_vp9.cpp"
#include <map>
#include <memory>

using namespace RyujinxMediaCodec;

// 解码器管理器
class DecoderManager {
private:
    std::map<int64_t, std::unique_ptr<IMediaCodecDecoder>> decoders_;
    int64_t nextId_ = 1;
    std::mutex mutex_;
    
public:
    static DecoderManager& GetInstance() {
        static DecoderManager instance;
        return instance;
    }
    
    int64_t CreateDecoder(VideoCodec codec) {
        std::lock_guard<std::mutex> lock(mutex_);
        
        std::unique_ptr<IMediaCodecDecoder> decoder;
        
        switch (codec) {
            case VideoCodec::H264:
                decoder = std::make_unique<MediaCodecH264Decoder>();
                break;
            case VideoCodec::VP8:
                decoder = std::make_unique<MediaCodecVP8Decoder>();
                break;
            case VideoCodec::VP9:
                decoder = std::make_unique<MediaCodecVP9Decoder>();
                break;
            default:
                return 0;
        }
        
        int64_t id = nextId_++;
        decoders_[id] = std::move(decoder);
        return id;
    }
    
    IMediaCodecDecoder* GetDecoder(int64_t id) {
        std::lock_guard<std::mutex> lock(mutex_);
        auto it = decoders_.find(id);
        return (it != decoders_.end()) ? it->second.get() : nullptr;
    }
    
    bool RemoveDecoder(int64_t id) {
        std::lock_guard<std::mutex> lock(mutex_);
        return decoders_.erase(id) > 0;
    }
    
    void ClearAll() {
        std::lock_guard<std::mutex> lock(mutex_);
        decoders_.clear();
    }
};

// C接口
extern "C" {

// 创建解码器
JNIEXPORT jlong JNICALL
Java_org_ryujinx_android_NativeHelpers_createMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jint codec_type) {
    
    VideoCodec codec = static_cast<VideoCodec>(codec_type);
    int64_t id = DecoderManager::GetInstance().CreateDecoder(codec);
    return static_cast<jlong>(id);
}

// 初始化解码器
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_initMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id,
    jint width, jint height,
    jint frame_rate,
    jint color_format,
    jbyteArray csd0,
    jbyteArray csd1,
    jbyteArray csd2) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return JNI_FALSE;
    }
    
    DecoderConfig config;
    config.width = width;
    config.height = height;
    config.frameRate = frame_rate;
    config.colorFormat = static_cast<ColorFormat>(color_format);
    
    // 复制 CSD 数据
    if (csd0) {
        jsize len = env->GetArrayLength(csd0);
        config.csd0.resize(len);
        jbyte* data = env->GetByteArrayElements(csd0, nullptr);
        if (data) {
            memcpy(config.csd0.data(), data, len);
            env->ReleaseByteArrayElements(csd0, data, JNI_ABORT);
        }
    }
    
    if (csd1) {
        jsize len = env->GetArrayLength(csd1);
        config.csd1.resize(len);
        jbyte* data = env->GetByteArrayElements(csd1, nullptr);
        if (data) {
            memcpy(config.csd1.data(), data, len);
            env->ReleaseByteArrayElements(csd1, data, JNI_ABORT);
        }
    }
    
    if (csd2) {
        jsize len = env->GetArrayLength(csd2);
        config.csd2.resize(len);
        jbyte* data = env->GetByteArrayElements(csd2, nullptr);
        if (data) {
            memcpy(config.csd2.data(), data, len);
            env->ReleaseByteArrayElements(csd2, data, JNI_ABORT);
        }
    }
    
    bool result = decoder->Initialize(config);
    return result ? JNI_TRUE : JNI_FALSE;
}

// 启动解码器
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_startMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return JNI_FALSE;
    }
    
    bool result = decoder->Start();
    return result ? JNI_TRUE : JNI_FALSE;
}

// 解码帧
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_decodeMediaCodecFrame(
    JNIEnv* env, jobject thiz,
    jlong decoder_id,
    jbyteArray frame_data,
    jlong presentation_time_us,
    jint flags) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return JNI_FALSE;
    }
    
    jsize len = env->GetArrayLength(frame_data);
    jbyte* data = env->GetByteArrayElements(frame_data, nullptr);
    if (!data) {
        return JNI_FALSE;
    }
    
    bool result = decoder->DecodeFrame(reinterpret_cast<uint8_t*>(data), 
                                      len, presentation_time_us, flags);
    
    env->ReleaseByteArrayElements(frame_data, data, JNI_ABORT);
    return result ? JNI_TRUE : JNI_FALSE;
}

// 获取解码帧
JNIEXPORT jbyteArray JNICALL
Java_org_ryujinx_android_NativeHelpers_getDecodedFrameYUV(
    JNIEnv* env, jobject thiz,
    jlong decoder_id,
    jint timeout_us,
    jintArray dimensions) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return nullptr;
    }
    
    std::vector<uint8_t> yuvData;
    int width, height;
    
    bool result = decoder->GetYUVData(yuvData, width, height, timeout_us);
    if (!result || yuvData.empty()) {
        return nullptr;
    }
    
    // 返回维度
    jint dims[2] = {width, height};
    env->SetIntArrayRegion(dimensions, 0, 2, dims);
    
    // 返回 YUV 数据
    jbyteArray resultArray = env->NewByteArray(yuvData.size());
    env->SetByteArrayRegion(resultArray, 0, yuvData.size(), 
                           reinterpret_cast<const jbyte*>(yuvData.data()));
    
    return resultArray;
}

// 停止解码器
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_stopMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return JNI_FALSE;
    }
    
    bool result = decoder->Stop();
    return result ? JNI_TRUE : JNI_FALSE;
}

// 销毁解码器
JNIEXPORT void JNICALL
Java_org_ryujinx_android_NativeHelpers_destroyMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    
    DecoderManager::GetInstance().RemoveDecoder(decoder_id);
}

// 检查编解码器支持
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_isMediaCodecSupported(
    JNIEnv* env, jobject thiz,
    jint codec_type) {
    
    VideoCodec codec = static_cast<VideoCodec>(codec_type);
    bool supported = MediaCodecUtils::IsCodecSupported(codec);
    return supported ? JNI_TRUE : JNI_FALSE;
}

// 获取设备信息
JNIEXPORT jstring JNICALL
Java_org_ryujinx_android_NativeHelpers_getMediaCodecDeviceInfo(
    JNIEnv* env, jobject thiz) {
    
    std::string info = MediaCodecUtils::GetDeviceInfo();
    return env->NewStringUTF(info.c_str());
}

// 获取解码器状态
JNIEXPORT jint JNICALL
Java_org_ryujinx_android_NativeHelpers_getDecoderStatus(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return static_cast<jint>(DecoderStatus::ERROR);
    }
    
    return static_cast<jint>(decoder->GetStatus());
}

// 刷新解码器
JNIEXPORT jboolean JNICALL
Java_org_ryujinx_android_NativeHelpers_flushMediaCodecDecoder(
    JNIEnv* env, jobject thiz,
    jlong decoder_id) {
    
    auto decoder = DecoderManager::GetInstance().GetDecoder(decoder_id);
    if (!decoder) {
        return JNI_FALSE;
    }
    
    bool result = decoder->Flush();
    return result ? JNI_TRUE : JNI_FALSE;
}

} // extern "C"