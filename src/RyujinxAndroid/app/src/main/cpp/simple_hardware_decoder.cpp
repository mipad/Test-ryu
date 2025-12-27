// simple_hardware_decoder.cpp
#include <jni.h>
#include <string>
#include <memory>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/avutil.h>
}

// 极简结构体
struct SimpleContext {
    AVCodecContext* codec_ctx;
    AVFrame* frame;
    bool hw_enabled;
};

// 创建解码器
extern "C" JNIEXPORT jlong JNICALL
Java_org_yourpackage_SimpleHardwareDecoder_create(
    JNIEnv* env, jobject thiz, 
    jint codec_type, jint width, jint height, jboolean use_hw) {
    
    try {
        auto ctx = new SimpleContext();
        
        // 根据codec_type查找解码器
        const char* codec_name = nullptr;
        switch (codec_type) {
            case 0: codec_name = "h264_mediacodec"; break;
            case 1: codec_name = "vp8_mediacodec"; break;
            case 2: codec_name = "vp9_mediacodec"; break;
            default: codec_name = "h264_mediacodec";
        }
        
        const AVCodec* codec = avcodec_find_decoder_by_name(codec_name);
        if (!codec) {
            // 回退到普通解码器
            switch (codec_type) {
                case 0: codec = avcodec_find_decoder(AV_CODEC_ID_H264); break;
                case 1: codec = avcodec_find_decoder(AV_CODEC_ID_VP8); break;
                case 2: codec = avcodec_find_decoder(AV_CODEC_ID_VP9); break;
            }
        }
        
        if (!codec) return 0;
        
        ctx->codec_ctx = avcodec_alloc_context3(codec);
        ctx->frame = av_frame_alloc();
        ctx->hw_enabled = use_hw;
        
        // 尝试硬件加速
        if (use_hw) {
            ctx->codec_ctx->hw_device_ctx = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_MEDIACODEC);
        }
        
        if (avcodec_open2(ctx->codec_ctx, codec, nullptr) < 0) {
            delete ctx;
            return 0;
        }
        
        return reinterpret_cast<jlong>(ctx);
    } catch (...) {
        return 0;
    }
}

// 解码
extern "C" JNIEXPORT jint JNICALL
Java_org_yourpackage_SimpleHardwareDecoder_decode(
    JNIEnv* env, jobject thiz, 
    jlong handle, jbyteArray data, jobject frame) {
    
    if (!handle) return -1;
    
    auto ctx = reinterpret_cast<SimpleContext*>(handle);
    
    // 创建AVPacket
    AVPacket* pkt = av_packet_alloc();
    jbyte* data_ptr = env->GetByteArrayElements(data, nullptr);
    jsize data_len = env->GetArrayLength(data);
    
    pkt->data = reinterpret_cast<uint8_t*>(data_ptr);
    pkt->size = data_len;
    
    // 发送包
    int ret = avcodec_send_packet(ctx->codec_ctx, pkt);
    if (ret < 0) {
        av_packet_free(&pkt);
        env->ReleaseByteArrayElements(data, data_ptr, JNI_ABORT);
        return ret;
    }
    
    // 接收帧
    ret = avcodec_receive_frame(ctx->codec_ctx, ctx->frame);
    if (ret < 0) {
        av_packet_free(&pkt);
        env->ReleaseByteArrayElements(data, data_ptr, JNI_ABORT);
        return ret;
    }
    
    // 填充Java帧对象
    jclass frameClass = env->GetObjectClass(frame);
    
    // 设置宽度和高度
    env->SetIntField(frame, env->GetFieldID(frameClass, "width", "I"), ctx->frame->width);
    env->SetIntField(frame, env->GetFieldID(frameClass, "height", "I"), ctx->frame->height);
    
    // 设置平面数据
    jfieldID dataField = env->GetFieldID(frameClass, "data", "[J");
    jfieldID linesizeField = env->GetFieldID(frameClass, "linesize", "[I");
    
    jlongArray dataArray = env->NewLongArray(3);
    jintArray linesizeArray = env->NewIntArray(3);
    
    jlong data_values[3] = {
        reinterpret_cast<jlong>(ctx->frame->data[0]),
        reinterpret_cast<jlong>(ctx->frame->data[1]),
        reinterpret_cast<jlong>(ctx->frame->data[2])
    };
    
    jint linesize_values[3] = {
        ctx->frame->linesize[0],
        ctx->frame->linesize[1],
        ctx->frame->linesize[2]
    };
    
    env->SetLongArrayRegion(dataArray, 0, 3, data_values);
    env->SetIntArrayRegion(linesizeArray, 0, 3, linesize_values);
    
    env->SetObjectField(frame, dataField, dataArray);
    env->SetObjectField(frame, linesizeField, linesizeArray);
    
    // 清理
    av_packet_free(&pkt);
    env->ReleaseByteArrayElements(data, data_ptr, JNI_ABORT);
    
    return 0;
}

// 销毁解码器
extern "C" JNIEXPORT void JNICALL
Java_org_yourpackage_SimpleHardwareDecoder_destroy(
    JNIEnv* env, jobject thiz, jlong handle) {
    
    if (!handle) return;
    
    auto ctx = reinterpret_cast<SimpleContext*>(handle);
    
    avcodec_free_context(&ctx->codec_ctx);
    av_frame_free(&ctx->frame);
    delete ctx;
}
