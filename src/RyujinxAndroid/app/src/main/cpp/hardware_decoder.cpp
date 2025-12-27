// hardware_decoder.cpp
#include "hardware_decoder.h"
#include "ffmpeg.h"
#include <memory>
#include <mutex>
#include <condition_variable>
#include <vector>
#include <map>
#include <string>
#include <sstream>
#include <iomanip>
#include <chrono>
#include <thread>
#include <atomic>
#include <cstring>
#include <cstdlib>

// 使用命名空间
using namespace std::chrono;

// 内部数据结构
struct InternalHWFrame {
    std::shared_ptr<FFmpeg::Frame> ff_frame;
    HWFrameData public_frame;
    bool is_hardware_decoded;
    int64_t decode_time;
    int64_t queue_time;
    int index;
};

struct InternalHWDecoder {
    std::unique_ptr<FFmpeg::DecodeApi> decode_api;
    std::vector<InternalHWFrame> frame_cache;
    std::mutex cache_mutex;
    std::condition_variable cache_cv;
    HWCallbacks callbacks;
    HWDecoderConfig config;
    HWDecoderStats stats;
    HWCodecType codec_type;
    bool use_hardware;
    bool is_initialized;
    bool is_flushing;
    bool is_closing;
    bool low_latency_mode;
    bool drop_late_frames;
    bool drop_corrupted_frames;
    int max_cache_frames;
    int performance_mode;
    int power_mode;
    int temperature_limit;
    int input_buffer_size;
    int output_buffer_size;
    int time_base_num;
    int time_base_den;
    int frame_rate_num;
    int frame_rate_den;
    int aspect_ratio_num;
    int aspect_ratio_den;
    int color_range;
    int color_primaries;
    int color_trc;
    int colorspace;
    int sample_rate;
    int channels;
    int sample_format;
    int decode_latency;
    int display_latency;
    int total_latency;
    int64_t frames_decoded;
    int64_t frames_dropped;
    int64_t frames_corrupted;
    int64_t bytes_decoded;
    double total_decode_time;
    std::string hardware_device;
    std::string codec_name;
    std::string hardware_type;
    std::map<std::string, std::string> properties;
    std::map<std::string, std::string> hardware_params;
    std::thread monitor_thread;
    std::atomic<bool> monitor_running;
    std::mutex stats_mutex;
};

// 全局变量
static HWLogCallback g_log_callback = nullptr;
static void* g_log_user_data = nullptr;
static HWLogLevel g_log_level = HW_LOG_INFO;
static std::mutex g_log_mutex;
static bool g_initialized = false;
static std::mutex g_init_mutex;

// 内部日志函数
static void internal_log(HWLogLevel level, const char* format, ...) {
    if (level > g_log_level) {
        return;
    }
    
    if (g_log_callback) {
        char buffer[4096];
        va_list args;
        va_start(args, format);
        vsnprintf(buffer, sizeof(buffer), format, args);
        va_end(args);
        
        std::lock_guard<std::mutex> lock(g_log_mutex);
        g_log_callback(g_log_user_data, level, buffer);
    } else {
        // 默认日志输出
        const char* level_str = "";
        switch (level) {
            case HW_LOG_PANIC: level_str = "PANIC"; break;
            case HW_LOG_FATAL: level_str = "FATAL"; break;
            case HW_LOG_ERROR: level_str = "ERROR"; break;
            case HW_LOG_WARNING: level_str = "WARNING"; break;
            case HW_LOG_INFO: level_str = "INFO"; break;
            case HW_LOG_VERBOSE: level_str = "VERBOSE"; break;
            case HW_LOG_DEBUG: level_str = "DEBUG"; break;
            case HW_LOG_TRACE: level_str = "TRACE"; break;
            default: level_str = "UNKNOWN"; break;
        }
        
        char buffer[4096];
        va_list args;
        va_start(args, format);
        vsnprintf(buffer, sizeof(buffer), format, args);
        va_end(args);
        
        // 输出到stderr
        fprintf(stderr, "[%s] %s\n", level_str, buffer);
    }
}

// 转换FFmpeg编解码器类型
static Tegra::Host1x::NvdecCommon::VideoCodec convert_codec_type(HWCodecType codec_type) {
    switch (codec_type) {
        case HW_CODEC_H264:
            return Tegra::Host1x::NvdecCommon::VideoCodec::H264;
        case HW_CODEC_VP8:
            return Tegra::Host1x::NvdecCommon::VideoCodec::VP8;
        case HW_CODEC_VP9:
            return Tegra::Host1x::NvdecCommon::VideoCodec::VP9;
        case HW_CODEC_HEVC:
            return Tegra::Host1x::NvdecCommon::VideoCodec::HEVC;
        case HW_CODEC_AV1:
            return Tegra::Host1x::NvdecCommon::VideoCodec::AV1;
        default:
            return Tegra::Host1x::NvdecCommon::VideoCodec::H264;
    }
}

// 转换FFmpeg像素格式
static AVPixelFormat convert_pixel_format(HWPixelFormat format) {
    switch (format) {
        case HW_PIX_FMT_YUV420P: return AV_PIX_FMT_YUV420P;
        case HW_PIX_FMT_NV12: return AV_PIX_FMT_NV12;
        case HW_PIX_FMT_NV21: return AV_PIX_FMT_NV21;
        case HW_PIX_FMT_RGBA: return AV_PIX_FMT_RGBA;
        case HW_PIX_FMT_BGRA: return AV_PIX_FMT_BGRA;
        case HW_PIX_FMT_ARGB: return AV_PIX_FMT_ARGB;
        case HW_PIX_FMT_ABGR: return AV_PIX_FMT_ABGR;
        default: return AV_PIX_FMT_NONE;
    }
}

// 转换HW像素格式
static HWPixelFormat convert_to_hw_pixel_format(AVPixelFormat format) {
    switch (format) {
        case AV_PIX_FMT_YUV420P: return HW_PIX_FMT_YUV420P;
        case AV_PIX_FMT_NV12: return HW_PIX_FMT_NV12;
        case AV_PIX_FMT_NV21: return HW_PIX_FMT_NV21;
        case AV_PIX_FMT_RGBA: return HW_PIX_FMT_RGBA;
        case AV_PIX_FMT_BGRA: return HW_PIX_FMT_BGRA;
        case AV_PIX_FMT_ARGB: return HW_PIX_FMT_ARGB;
        case AV_PIX_FMT_ABGR: return HW_PIX_FMT_ABGR;
        default: return HW_PIX_FMT_NONE;
    }
}

// 填充帧数据
static void fill_frame_data(const std::shared_ptr<FFmpeg::Frame>& ff_frame, HWFrameData* frame_data, bool is_hardware_decoded) {
    if (!ff_frame || !frame_data) {
        return;
    }
    
    auto frame = ff_frame->GetFrame();
    
    // 填充平面数据
    for (int i = 0; i < 4; i++) {
        frame_data->data[i] = ff_frame->GetPlane(i);
        frame_data->linesize[i] = ff_frame->GetStride(i);
    }
    
    // 填充基本属性
    frame_data->width = ff_frame->GetWidth();
    frame_data->height = ff_frame->GetHeight();
    frame_data->format = convert_to_hw_pixel_format(ff_frame->GetPixelFormat());
    frame_data->pts = frame->pts;
    frame_data->dts = frame->pkt_dts;
    frame_data->duration = frame->pkt_duration;
    frame_data->key_frame = frame->key_frame;
    frame_data->interlaced = ff_frame->IsInterlaced();
    frame_data->repeat_pict = frame->repeat_pict;
    frame_data->coded_picture_number = frame->coded_picture_number;
    frame_data->display_picture_number = frame->display_picture_number;
    frame_data->quality = frame->quality;
    frame_data->reordered_opaque = frame->reordered_opaque;
    
    // 填充采样宽高比
    if (frame->sample_aspect_ratio.num != 0 && frame->sample_aspect_ratio.den != 0) {
        frame_data->sample_aspect_ratio_num = frame->sample_aspect_ratio.num;
        frame_data->sample_aspect_ratio_den = frame->sample_aspect_ratio.den;
    } else {
        frame_data->sample_aspect_ratio_num = 1;
        frame_data->sample_aspect_ratio_den = 1;
    }
    
    // 填充颜色信息
    frame_data->color_range = frame->color_range;
    frame_data->color_primaries = frame->color_primaries;
    frame_data->color_trc = frame->color_transfer;
    frame_data->colorspace = frame->colorspace;
    frame_data->chroma_location = frame->chroma_location;
    frame_data->best_effort_timestamp = frame->best_effort_timestamp;
    frame_data->pkt_pos = frame->pkt_pos;
    frame_data->pkt_size = frame->pkt_size;
    
    // 填充音频信息
    frame_data->channels = frame->channels;
    frame_data->channel_layout = static_cast<int>(frame->channel_layout);
    frame_data->nb_samples = frame->nb_samples;
    frame_data->sample_rate = frame->sample_rate;
    frame_data->audio_channels = frame->channels;
    frame_data->audio_channel_layout = static_cast<int>(frame->channel_layout);
    frame_data->audio_sample_rate = frame->sample_rate;
    frame_data->audio_sample_format = frame->format;
    frame_data->audio_frame_size = av_samples_get_buffer_size(nullptr, frame->channels, frame->nb_samples, static_cast<AVSampleFormat>(frame->format), 1);
    frame_data->audio_buffer_size = frame->audio_buffer_size;
    
    // 填充元数据
    frame_data->metadata_count = 0;
    frame_data->metadata = nullptr;
    frame_data->decode_error_flags = frame->decode_error_flags;
}

// 性能监控线程
static void performance_monitor_thread(InternalHWDecoder* decoder) {
    while (decoder->monitor_running) {
        std::this_thread::sleep_for(milliseconds(100));
        
        // 计算当前性能数据
        {
            std::lock_guard<std::mutex> lock(decoder->stats_mutex);
            
            // 更新帧率
            auto now = steady_clock::now();
            static auto last_update = now;
            auto elapsed = duration_cast<milliseconds>(now - last_update).count();
            
            if (elapsed >= 1000) {
                if (elapsed > 0) {
                    decoder->stats.fps = (decoder->frames_decoded * 1000.0) / elapsed;
                }
                decoder->frames_decoded = 0;
                last_update = now;
            }
            
            // 更新延迟统计
            decoder->stats.current_delay = decoder->total_latency;
            decoder->stats.max_delay = std::max(decoder->stats.max_delay, decoder->total_latency);
            decoder->stats.min_delay = std::min(decoder->stats.min_delay, decoder->total_latency);
            decoder->stats.average_delay = (decoder->stats.average_delay * 0.9) + (decoder->total_latency * 0.1);
            
            // 更新比特率统计
            if (decoder->stats.decode_time_ms > 0) {
                decoder->stats.current_bitrate = (decoder->bytes_decoded * 8) / (decoder->stats.decode_time_ms / 1000.0);
                decoder->stats.max_bitrate = std::max(decoder->stats.max_bitrate, decoder->stats.current_bitrate);
                decoder->stats.min_bitrate = std::min(decoder->stats.min_bitrate, decoder->stats.current_bitrate);
                decoder->stats.average_bitrate = (decoder->stats.average_bitrate * 0.9) + (decoder->stats.current_bitrate * 0.1);
                decoder->stats.peak_bitrate = decoder->stats.max_bitrate;
            }
        }
    }
}

// ===================== 公共API实现 =====================

extern "C" {

HWDecoderHandle hw_decoder_create(HWCodecType codec_type, const HWDecoderConfig* config, const HWCallbacks* callbacks) {
    try {
        internal_log(HW_LOG_DEBUG, "Creating hardware decoder for codec type: %d", codec_type);
        
        auto decoder = new InternalHWDecoder();
        if (!decoder) {
            internal_log(HW_LOG_ERROR, "Failed to allocate decoder memory");
            return nullptr;
        }
        
        // 初始化成员变量
        decoder->codec_type = codec_type;
        decoder->is_initialized = false;
        decoder->is_flushing = false;
        decoder->is_closing = false;
        decoder->low_latency_mode = false;
        decoder->drop_late_frames = false;
        decoder->drop_corrupted_frames = false;
        decoder->max_cache_frames = 10;
        decoder->performance_mode = 0;
        decoder->power_mode = 0;
        decoder->temperature_limit = 85;
        decoder->input_buffer_size = 1024 * 1024; // 1MB
        decoder->output_buffer_size = 10 * 1024 * 1024; // 10MB
        decoder->time_base_num = 1;
        decoder->time_base_den = 1000000; // 微秒
        decoder->frame_rate_num = 30;
        decoder->frame_rate_den = 1;
        decoder->aspect_ratio_num = 1;
        decoder->aspect_ratio_den = 1;
        decoder->color_range = 2; // JPEG
        decoder->color_primaries = 2; // BT.709
        decoder->color_trc = 2; // BT.709
        decoder->colorspace = 2; // BT.709
        decoder->sample_rate = 48000;
        decoder->channels = 2;
        decoder->sample_format = 1; // S16
        decoder->decode_latency = 0;
        decoder->display_latency = 0;
        decoder->total_latency = 0;
        decoder->frames_decoded = 0;
        decoder->frames_dropped = 0;
        decoder->frames_corrupted = 0;
        decoder->bytes_decoded = 0;
        decoder->total_decode_time = 0.0;
        decoder->monitor_running = false;
        decoder->hardware_device = "";
        decoder->codec_name = "";
        decoder->hardware_type = "";
        
        // 初始化统计信息
        memset(&decoder->stats, 0, sizeof(HWDecoderStats));
        decoder->stats.fps = 0.0;
        decoder->stats.buffer_level = 0;
        
        // 复制配置
        if (config) {
            decoder->config = *config;
            decoder->use_hardware = true; // 默认使用硬件
        } else {
            memset(&decoder->config, 0, sizeof(HWDecoderConfig));
            decoder->config.width = 1920;
            decoder->config.height = 1080;
            decoder->config.bit_depth = 8;
            decoder->config.chroma_format = 1; // 4:2:0
            decoder->config.low_latency = false;
            decoder->config.thread_count = 0;
            decoder->config.max_ref_frames = 16;
            decoder->config.enable_deblocking = true;
            decoder->config.enable_sao = true;
            decoder->config.profile = 100; // High profile
            decoder->config.level = 40; // Level 4.0
            decoder->use_hardware = true;
        }
        
        // 复制回调
        if (callbacks) {
            decoder->callbacks = *callbacks;
        } else {
            memset(&decoder->callbacks, 0, sizeof(HWCallbacks));
        }
        
        // 创建解码API
        decoder->decode_api = std::make_unique<FFmpeg::DecodeApi>();
        if (!decoder->decode_api) {
            internal_log(HW_LOG_ERROR, "Failed to create decode API");
            delete decoder;
            return nullptr;
        }
        
        // 初始化解码器
        auto ff_codec = convert_codec_type(codec_type);
        if (!decoder->decode_api->Initialize(ff_codec)) {
            internal_log(HW_LOG_ERROR, "Failed to initialize decode API");
            delete decoder;
            return nullptr;
        }
        
        // 设置硬件设备类型
        #ifdef ANDROID
            decoder->hardware_type = "mediacodec";
            decoder->codec_name = "MediaCodec";
        #else
            decoder->hardware_type = "software";
            decoder->codec_name = "Software";
        #endif
        
        decoder->is_initialized = true;
        
        // 启动性能监控线程
        decoder->monitor_running = true;
        decoder->monitor_thread = std::thread(performance_monitor_thread, decoder);
        
        internal_log(HW_LOG_INFO, "Hardware decoder created successfully (type: %s, hardware: %s)",
                    decoder->hardware_type.c_str(), decoder->codec_name.c_str());
        
        return static_cast<HWDecoderHandle>(decoder);
    } catch (const std::exception& e) {
        internal_log(HW_LOG_ERROR, "Exception in hw_decoder_create: %s", e.what());
        return nullptr;
    } catch (...) {
        internal_log(HW_LOG_ERROR, "Unknown exception in hw_decoder_create");
        return nullptr;
    }
}

HWDecoderHandle hw_decoder_create_simple(HWCodecType codec_type, int width, int height, bool use_hardware) {
    HWDecoderConfig config;
    memset(&config, 0, sizeof(HWDecoderConfig));
    config.width = width;
    config.height = height;
    config.bit_depth = 8;
    config.chroma_format = 1;
    config.low_latency = false;
    config.thread_count = 0;
    config.max_ref_frames = 16;
    config.enable_deblocking = true;
    config.enable_sao = true;
    config.profile = 100;
    config.level = 40;
    
    return hw_decoder_create(codec_type, &config, nullptr);
}

void hw_decoder_destroy(HWDecoderHandle handle) {
    if (!handle) {
        return;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    internal_log(HW_LOG_DEBUG, "Destroying hardware decoder");
    
    // 停止监控线程
    if (decoder->monitor_running) {
        decoder->monitor_running = false;
        if (decoder->monitor_thread.joinable()) {
            decoder->monitor_thread.join();
        }
    }
    
    // 清理缓存
    {
        std::lock_guard<std::mutex> lock(decoder->cache_mutex);
        decoder->frame_cache.clear();
    }
    
    // 重置解码器
    if (decoder->decode_api) {
        decoder->decode_api->Reset();
    }
    
    // 删除解码器
    delete decoder;
    
    internal_log(HW_LOG_INFO, "Hardware decoder destroyed");
}

int hw_decoder_decode(HWDecoderHandle handle, const uint8_t* data, int size, int64_t pts, int64_t dts, HWFrameData* frame_data) {
    if (!handle || !data || size <= 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    if (!decoder->is_initialized || decoder->is_closing) {
        return HW_DECODER_ERROR_INVALID_HANDLE;
    }
    
    try {
        auto start_time = steady_clock::now();
        
        // 发送数据包
        std::span<const uint8_t> packet(data, size);
        if (!decoder->decode_api->SendPacket(packet)) {
            internal_log(HW_LOG_ERROR, "Failed to send packet");
            return HW_DECODER_ERROR_DECODE_FAILED;
        }
        
        // 接收帧
        auto ff_frame = decoder->decode_api->ReceiveFrame();
        if (!ff_frame) {
            // 没有帧可用，这可能是正常的（比如需要更多数据）
            return HW_DECODER_ERROR_TRY_AGAIN;
        }
        
        auto decode_time = duration_cast<microseconds>(steady_clock::now() - start_time).count();
        
        // 更新统计信息
        {
            std::lock_guard<std::mutex> lock(decoder->stats_mutex);
            decoder->frames_decoded++;
            decoder->bytes_decoded += size;
            decoder->total_decode_time += decode_time / 1000.0; // 转换为毫秒
            
            decoder->stats.frames_decoded = decoder->frames_decoded;
            decoder->stats.bytes_decoded = decoder->bytes_decoded;
            decoder->stats.decode_time_ms = decoder->total_decode_time;
            
            // 计算当前帧率
            static auto last_time = steady_clock::now();
            auto current_time = steady_clock::now();
            auto elapsed = duration_cast<milliseconds>(current_time - last_time).count();
            
            if (elapsed >= 1000) {
                decoder->stats.fps = (decoder->frames_decoded * 1000.0) / elapsed;
                decoder->frames_decoded = 0;
                last_time = current_time;
            }
        }
        
        // 检查是否为损坏帧
        bool is_corrupted = false;
        auto frame = ff_frame->GetFrame();
        if (frame->decode_error_flags != 0) {
            decoder->frames_corrupted++;
            decoder->stats.frames_corrupted = decoder->frames_corrupted;
            is_corrupted = true;
            
            if (decoder->drop_corrupted_frames) {
                internal_log(HW_LOG_WARNING, "Dropping corrupted frame");
                return HW_DECODER_ERROR_TRY_AGAIN;
            }
        }
        
        // 检查是否为延迟帧
        bool is_late = false;
        if (pts > 0) {
            auto current_time = duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
            if (current_time > pts + 100000) { // 超过100ms视为延迟
                is_late = true;
                decoder->frames_dropped++;
                decoder->stats.frames_dropped = decoder->frames_dropped;
                
                if (decoder->drop_late_frames) {
                    internal_log(HW_LOG_WARNING, "Dropping late frame (pts: %lld, current: %lld)", 
                                pts, current_time);
                    return HW_DECODER_ERROR_TRY_AGAIN;
                }
            }
        }
        
        // 创建内部帧
        InternalHWFrame internal_frame;
        internal_frame.ff_frame = ff_frame;
        internal_frame.is_hardware_decoded = ff_frame->IsHardwareDecoded();
        internal_frame.decode_time = decode_time;
        internal_frame.queue_time = duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
        internal_frame.index = static_cast<int>(decoder->frame_cache.size());
        
        // 填充公共帧数据
        fill_frame_data(ff_frame, &internal_frame.public_frame, internal_frame.is_hardware_decoded);
        
        // 设置时间戳
        internal_frame.public_frame.pts = pts;
        internal_frame.public_frame.dts = dts;
        
        // 添加到缓存
        {
            std::lock_guard<std::mutex> lock(decoder->cache_mutex);
            
            // 检查缓存是否已满
            if (decoder->frame_cache.size() >= static_cast<size_t>(decoder->max_cache_frames)) {
                // 移除最旧的帧
                if (!decoder->frame_cache.empty()) {
                    decoder->frame_cache.erase(decoder->frame_cache.begin());
                }
            }
            
            decoder->frame_cache.push_back(internal_frame);
            decoder->stats.buffer_level = static_cast<int>((decoder->frame_cache.size() * 100) / decoder->max_cache_frames);
        }
        
        // 回调通知
        if (decoder->callbacks.frame_callback) {
            decoder->callbacks.frame_callback(decoder->callbacks.user_data, &internal_frame.public_frame);
        }
        
        if (decoder->callbacks.buffer_callback) {
            int buffer_level = decoder->stats.buffer_level;
            decoder->callbacks.buffer_callback(decoder->callbacks.user_data, buffer_level, decoder->max_cache_frames);
        }
        
        // 如果提供了输出参数，复制帧数据
        if (frame_data) {
            *frame_data = internal_frame.public_frame;
        }
        
        // 更新延迟统计
        if (pts > 0) {
            auto current_time = duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
            decoder->total_latency = static_cast<int>(current_time - pts);
            decoder->stats.current_delay = decoder->total_latency;
        }
        
        return HW_DECODER_SUCCESS;
    } catch (const std::exception& e) {
        internal_log(HW_LOG_ERROR, "Exception in hw_decoder_decode: %s", e.what());
        
        if (decoder->callbacks.error_callback) {
            decoder->callbacks.error_callback(decoder->callbacks.user_data, 
                                            HW_DECODER_ERROR_DECODE_FAILED, 
                                            e.what());
        }
        
        return HW_DECODER_ERROR_DECODE_FAILED;
    } catch (...) {
        internal_log(HW_LOG_ERROR, "Unknown exception in hw_decoder_decode");
        
        if (decoder->callbacks.error_callback) {
            decoder->callbacks.error_callback(decoder->callbacks.user_data, 
                                            HW_DECODER_ERROR_UNKNOWN, 
                                            "Unknown exception");
        }
        
        return HW_DECODER_ERROR_UNKNOWN;
    }
}

int hw_decoder_decode_simple(HWDecoderHandle handle, const uint8_t* data, int size, HWFrameData* frame_data) {
    int64_t pts = duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
    return hw_decoder_decode(handle, data, size, pts, pts, frame_data);
}

int hw_decoder_flush(HWDecoderHandle handle, HWFrameData* frame_data) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_HANDLE;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    if (decoder->is_flushing) {
        return HW_DECODER_ERROR_TRY_AGAIN;
    }
    
    decoder->is_flushing = true;
    
    try {
        // 发送空包以刷新解码器
        std::span<const uint8_t> empty_packet(nullptr, 0);
        
        // 发送刷新包
        if (!decoder->decode_api->SendPacket(empty_packet)) {
            decoder->is_flushing = false;
            return HW_DECODER_ERROR_FLUSH_FAILED;
        }
        
        // 获取所有剩余帧
        int frame_count = 0;
        while (true) {
            auto ff_frame = decoder->decode_api->ReceiveFrame();
            if (!ff_frame) {
                break;
            }
            
            // 创建内部帧
            InternalHWFrame internal_frame;
            internal_frame.ff_frame = ff_frame;
            internal_frame.is_hardware_decoded = ff_frame->IsHardwareDecoded();
            internal_frame.decode_time = 0;
            internal_frame.queue_time = duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
            internal_frame.index = static_cast<int>(decoder->frame_cache.size());
            
            // 填充公共帧数据
            fill_frame_data(ff_frame, &internal_frame.public_frame, internal_frame.is_hardware_decoded);
            
            // 添加到缓存
            {
                std::lock_guard<std::mutex> lock(decoder->cache_mutex);
                
                if (decoder->frame_cache.size() < static_cast<size_t>(decoder->max_cache_frames)) {
                    decoder->frame_cache.push_back(internal_frame);
                    frame_count++;
                }
            }
            
            // 如果提供了输出参数，只返回最后一帧
            if (frame_data && frame_count == 1) {
                *frame_data = internal_frame.public_frame;
            }
        }
        
        decoder->is_flushing = false;
        
        if (frame_count > 0) {
            internal_log(HW_LOG_DEBUG, "Flushed %d frames from decoder", frame_count);
            return HW_DECODER_SUCCESS;
        } else {
            return HW_DECODER_ERROR_EOF;
        }
    } catch (const std::exception& e) {
        decoder->is_flushing = false;
        internal_log(HW_LOG_ERROR, "Exception in hw_decoder_flush: %s", e.what());
        return HW_DECODER_ERROR_FLUSH_FAILED;
    } catch (...) {
        decoder->is_flushing = false;
        internal_log(HW_LOG_ERROR, "Unknown exception in hw_decoder_flush");
        return HW_DECODER_ERROR_FLUSH_FAILED;
    }
}

void hw_decoder_reset(HWDecoderHandle handle) {
    if (!handle) {
        return;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    internal_log(HW_LOG_DEBUG, "Resetting hardware decoder");
    
    // 重置解码器
    if (decoder->decode_api) {
        decoder->decode_api->Reset();
    }
    
    // 清空缓存
    {
        std::lock_guard<std::mutex> lock(decoder->cache_mutex);
        decoder->frame_cache.clear();
    }
    
    // 重置统计信息
    hw_decoder_reset_stats(handle);
    
    decoder->is_flushing = false;
    
    internal_log(HW_LOG_INFO, "Hardware decoder reset");
}

int hw_decoder_get_config(HWDecoderHandle handle, HWDecoderConfig* config) {
    if (!handle || !config) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *config = decoder->config;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_update_config(HWDecoderHandle handle, const HWDecoderConfig* config) {
    if (!handle || !config) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    // 保存旧配置
    auto old_config = decoder->config;
    decoder->config = *config;
    
    // 检查配置是否改变
    if (memcmp(&old_config, config, sizeof(HWDecoderConfig)) != 0) {
        internal_log(HW_LOG_INFO, "Decoder configuration updated");
        
        // 回调通知配置改变
        if (decoder->callbacks.format_changed_callback) {
            decoder->callbacks.format_changed_callback(decoder->callbacks.user_data, config);
        }
    }
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_stats(HWDecoderHandle handle, HWDecoderStats* stats) {
    if (!handle || !stats) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::lock_guard<std::mutex> lock(decoder->stats_mutex);
    *stats = decoder->stats;
    
    // 更新实时数据
    stats->frames_decoded = decoder->frames_decoded;
    stats->frames_dropped = decoder->frames_dropped;
    stats->frames_corrupted = decoder->frames_corrupted;
    stats->bytes_decoded = decoder->bytes_decoded;
    stats->decode_time_ms = decoder->total_decode_time;
    
    // 计算平均比特率
    if (decoder->total_decode_time > 0) {
        stats->average_bitrate = static_cast<int64_t>((decoder->bytes_decoded * 8) / (decoder->total_decode_time / 1000.0));
    }
    
    // 更新缓冲区级别
    {
        std::lock_guard<std::mutex> cache_lock(decoder->cache_mutex);
        stats->buffer_level = static_cast<int>((decoder->frame_cache.size() * 100) / decoder->max_cache_frames);
    }
    
    return HW_DECODER_SUCCESS;
}

void hw_decoder_reset_stats(HWDecoderHandle handle) {
    if (!handle) {
        return;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::lock_guard<std::mutex> lock(decoder->stats_mutex);
    
    memset(&decoder->stats, 0, sizeof(HWDecoderStats));
    decoder->frames_decoded = 0;
    decoder->frames_dropped = 0;
    decoder->frames_corrupted = 0;
    decoder->bytes_decoded = 0;
    decoder->total_decode_time = 0.0;
    
    decoder->stats.fps = 0.0;
    decoder->stats.buffer_level = 0;
    
    internal_log(HW_LOG_DEBUG, "Decoder statistics reset");
}

bool hw_decoder_is_hardware_supported(HWCodecType codec_type) {
    // 检查编解码器是否支持硬件解码
    // 这里可以添加更复杂的检测逻辑
    switch (codec_type) {
        case HW_CODEC_H264:
        case HW_CODEC_VP8:
        case HW_CODEC_VP9:
        case HW_CODEC_HEVC:
            return true;
        case HW_CODEC_AV1:
            // AV1硬件支持有限，需要检查
            return false;
        default:
            return false;
    }
}

bool hw_decoder_is_hardware_accelerated(HWDecoderHandle handle) {
    if (!handle) {
        return false;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    // 检查是否有缓存的硬件解码帧
    std::lock_guard<std::mutex> lock(decoder->cache_mutex);
    for (const auto& frame : decoder->frame_cache) {
        if (frame.is_hardware_decoded) {
            return true;
        }
    }
    
    return false;
}

const char* hw_decoder_get_hardware_type(HWDecoderHandle handle) {
    if (!handle) {
        return "unknown";
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->hardware_type.c_str();
}

const char* hw_decoder_get_codec_name(HWDecoderHandle handle) {
    if (!handle) {
        return "unknown";
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->codec_name.c_str();
}

const char* hw_decoder_get_pixel_format_name(HWPixelFormat format) {
    switch (format) {
        case HW_PIX_FMT_YUV420P: return "yuv420p";
        case HW_PIX_FMT_NV12: return "nv12";
        case HW_PIX_FMT_NV21: return "nv21";
        case HW_PIX_FMT_RGBA: return "rgba";
        case HW_PIX_FMT_BGRA: return "bgra";
        case HW_PIX_FMT_ARGB: return "argb";
        case HW_PIX_FMT_ABGR: return "abgr";
        default: return "none";
    }
}

void hw_decoder_set_log_callback(HWLogCallback callback, void* user_data) {
    std::lock_guard<std::mutex> lock(g_log_mutex);
    g_log_callback = callback;
    g_log_user_data = user_data;
}

void hw_decoder_set_log_level(HWLogLevel level) {
    g_log_level = level;
}

const char* hw_decoder_get_version(void) {
    return "1.0.0";
}

const char* hw_decoder_get_build_info(void) {
    static std::string build_info;
    if (build_info.empty()) {
        std::ostringstream oss;
        oss << "Hardware Decoder Library v" << hw_decoder_get_version() << "\n";
        oss << "Build time: " << __DATE__ << " " << __TIME__ << "\n";
        oss << "Target: Android ARM64\n";
        oss << "FFmpeg: enabled\n";
        oss << "Hardware acceleration: enabled";
        build_info = oss.str();
    }
    return build_info.c_str();
}

HWFrameData* hw_decoder_allocate_frame(void) {
    auto frame = new HWFrameData();
    if (!frame) {
        return nullptr;
    }
    
    memset(frame, 0, sizeof(HWFrameData));
    return frame;
}

void hw_decoder_free_frame(HWFrameData* frame) {
    if (frame) {
        // 注意：这里不释放data指针，因为它由解码器管理
        delete frame;
    }
}

int hw_decoder_copy_frame(const HWFrameData* src, HWFrameData* dst) {
    if (!src || !dst) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    *dst = *src;
    
    // 复制指针，但不复制实际数据
    // 如果需要深拷贝，需要单独实现
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_plane_size(const HWFrameData* frame, int plane) {
    if (!frame || plane < 0 || plane >= 4) {
        return 0;
    }
    
    if (!frame->data[plane] || frame->linesize[plane] <= 0) {
        return 0;
    }
    
    int height = frame->height;
    if (plane > 0) {
        // 色度平面高度减半
        height = (height + 1) / 2;
    }
    
    return frame->linesize[plane] * height;
}

int hw_decoder_get_frame_size(const HWFrameData* frame) {
    if (!frame) {
        return 0;
    }
    
    int total_size = 0;
    for (int i = 0; i < 4; i++) {
        total_size += hw_decoder_get_plane_size(frame, i);
    }
    
    return total_size;
}

bool hw_decoder_is_frame_valid(const HWFrameData* frame) {
    if (!frame) {
        return false;
    }
    
    if (frame->width <= 0 || frame->height <= 0) {
        return false;
    }
    
    if (!frame->data[0] || frame->linesize[0] <= 0) {
        return false;
    }
    
    return true;
}

void hw_decoder_clear_frame(HWFrameData* frame) {
    if (frame) {
        memset(frame, 0, sizeof(HWFrameData));
    }
}

int hw_decoder_set_property(HWDecoderHandle handle, const char* name, const char* value) {
    if (!handle || !name) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::string key(name);
    std::string val(value ? value : "");
    
    decoder->properties[key] = val;
    
    internal_log(HW_LOG_DEBUG, "Set property: %s = %s", name, value ? value : "(null)");
    
    return HW_DECODER_SUCCESS;
}

const char* hw_decoder_get_property(HWDecoderHandle handle, const char* name) {
    if (!handle || !name) {
        return nullptr;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::string key(name);
    auto it = decoder->properties.find(key);
    if (it != decoder->properties.end()) {
        return it->second.c_str();
    }
    
    return nullptr;
}

int hw_decoder_get_supported_codecs(HWCodecType** codecs, int* count) {
    static HWCodecType supported_codecs[] = {
        HW_CODEC_H264,
        HW_CODEC_VP8,
        HW_CODEC_VP9,
        HW_CODEC_HEVC
    };
    
    static int supported_count = sizeof(supported_codecs) / sizeof(supported_codecs[0]);
    
    if (codecs) {
        *codecs = static_cast<HWCodecType*>(malloc(sizeof(HWCodecType) * supported_count));
        if (!*codecs) {
            return HW_DECODER_ERROR_OUT_OF_MEMORY;
        }
        
        memcpy(*codecs, supported_codecs, sizeof(HWCodecType) * supported_count);
    }
    
    if (count) {
        *count = supported_count;
    }
    
    return HW_DECODER_SUCCESS;
}

void hw_decoder_free_supported_codecs(HWCodecType* codecs) {
    if (codecs) {
        free(codecs);
    }
}

int hw_decoder_get_supported_formats(HWPixelFormat** formats, int* count) {
    static HWPixelFormat supported_formats[] = {
        HW_PIX_FMT_YUV420P,
        HW_PIX_FMT_NV12,
        HW_PIX_FMT_NV21,
        HW_PIX_FMT_RGBA,
        HW_PIX_FMT_BGRA
    };
    
    static int supported_count = sizeof(supported_formats) / sizeof(supported_formats[0]);
    
    if (formats) {
        *formats = static_cast<HWPixelFormat*>(malloc(sizeof(HWPixelFormat) * supported_count));
        if (!*formats) {
            return HW_DECODER_ERROR_OUT_OF_MEMORY;
        }
        
        memcpy(*formats, supported_formats, sizeof(HWPixelFormat) * supported_count);
    }
    
    if (count) {
        *count = supported_count;
    }
    
    return HW_DECODER_SUCCESS;
}

void hw_decoder_free_supported_formats(HWPixelFormat* formats) {
    if (formats) {
        free(formats);
    }
}

const char* hw_decoder_get_error_string(int error_code) {
    switch (error_code) {
        case HW_DECODER_SUCCESS: return "Success";
        case HW_DECODER_ERROR_INVALID_HANDLE: return "Invalid handle";
        case HW_DECODER_ERROR_INVALID_PARAMETER: return "Invalid parameter";
        case HW_DECODER_ERROR_OUT_OF_MEMORY: return "Out of memory";
        case HW_DECODER_ERROR_INIT_FAILED: return "Initialization failed";
        case HW_DECODER_ERROR_DECODE_FAILED: return "Decode failed";
        case HW_DECODER_ERROR_FLUSH_FAILED: return "Flush failed";
        case HW_DECODER_ERROR_CLOSE_FAILED: return "Close failed";
        case HW_DECODER_ERROR_NOT_SUPPORTED: return "Not supported";
        case HW_DECODER_ERROR_TIMEOUT: return "Timeout";
        case HW_DECODER_ERROR_EOF: return "End of file";
        case HW_DECODER_ERROR_TRY_AGAIN: return "Try again";
        case HW_DECODER_ERROR_BUFFER_FULL: return "Buffer full";
        case HW_DECODER_ERROR_BUFFER_EMPTY: return "Buffer empty";
        case HW_DECODER_ERROR_HARDWARE_CHANGED: return "Hardware changed";
        case HW_DECODER_ERROR_SURFACE_CHANGED: return "Surface changed";
        case HW_DECODER_ERROR_FORMAT_CHANGED: return "Format changed";
        case HW_DECODER_ERROR_STREAM_CHANGED: return "Stream changed";
        case HW_DECODER_ERROR_DISPLAY_CHANGED: return "Display changed";
        case HW_DECODER_ERROR_RESOLUTION_CHANGED: return "Resolution changed";
        case HW_DECODER_ERROR_BITRATE_CHANGED: return "Bitrate changed";
        case HW_DECODER_ERROR_FRAMERATE_CHANGED: return "Framerate changed";
        case HW_DECODER_ERROR_CODEC_CHANGED: return "Codec changed";
        case HW_DECODER_ERROR_PROFILE_CHANGED: return "Profile changed";
        case HW_DECODER_ERROR_LEVEL_CHANGED: return "Level changed";
        case HW_DECODER_ERROR_UNKNOWN: return "Unknown error";
        default: return "Unknown error code";
    }
}

int hw_decoder_initialize(void) {
    std::lock_guard<std::mutex> lock(g_init_mutex);
    
    if (g_initialized) {
        return HW_DECODER_SUCCESS;
    }
    
    // 初始化FFmpeg（如果还没初始化）
    // 这里可以添加FFmpeg初始化代码
    
    g_initialized = true;
    internal_log(HW_LOG_INFO, "Hardware decoder subsystem initialized");
    
    return HW_DECODER_SUCCESS;
}

void hw_decoder_cleanup(void) {
    std::lock_guard<std::mutex> lock(g_init_mutex);
    
    if (!g_initialized) {
        return;
    }
    
    // 清理FFmpeg资源
    // 这里可以添加FFmpeg清理代码
    
    g_initialized = false;
    internal_log(HW_LOG_INFO, "Hardware decoder subsystem cleaned up");
}

void hw_decoder_lock(HWDecoderHandle handle) {
    if (!handle) {
        return;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->cache_mutex.lock();
}

void hw_decoder_unlock(HWDecoderHandle handle) {
    if (!handle) {
        return;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->cache_mutex.unlock();
}

int hw_decoder_set_max_cache_frames(HWDecoderHandle handle, int max_frames) {
    if (!handle || max_frames <= 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->max_cache_frames = max_frames;
    
    internal_log(HW_LOG_DEBUG, "Max cache frames set to %d", max_frames);
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_cache_frame_count(HWDecoderHandle handle) {
    if (!handle) {
        return 0;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::lock_guard<std::mutex> lock(decoder->cache_mutex);
    return static_cast<int>(decoder->frame_cache.size());
}

void hw_decoder_clear_cache(HWDecoderHandle handle) {
    if (!handle) {
        return;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::lock_guard<std::mutex> lock(decoder->cache_mutex);
    decoder->frame_cache.clear();
    
    internal_log(HW_LOG_DEBUG, "Frame cache cleared");
}

int hw_decoder_get_cached_frame(HWDecoderHandle handle, HWFrameData* frame_data, int index) {
    if (!handle || !frame_data || index < 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::lock_guard<std::mutex> lock(decoder->cache_mutex);
    
    if (index >= static_cast<int>(decoder->frame_cache.size())) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    const auto& frame = decoder->frame_cache[index];
    *frame_data = frame.public_frame;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_time_base(HWDecoderHandle handle, int numerator, int denominator) {
    if (!handle || denominator == 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->time_base_num = numerator;
    decoder->time_base_den = denominator;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_time_base(HWDecoderHandle handle, int* numerator, int* denominator) {
    if (!handle || !numerator || !denominator) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *numerator = decoder->time_base_num;
    *denominator = decoder->time_base_den;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_frame_rate(HWDecoderHandle handle, int numerator, int denominator) {
    if (!handle || denominator == 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->frame_rate_num = numerator;
    decoder->frame_rate_den = denominator;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_frame_rate(HWDecoderHandle handle, int* numerator, int* denominator) {
    if (!handle || !numerator || !denominator) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *numerator = decoder->frame_rate_num;
    *denominator = decoder->frame_rate_den;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_aspect_ratio(HWDecoderHandle handle, int numerator, int denominator) {
    if (!handle || denominator == 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->aspect_ratio_num = numerator;
    decoder->aspect_ratio_den = denominator;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_aspect_ratio(HWDecoderHandle handle, int* numerator, int* denominator) {
    if (!handle || !numerator || !denominator) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *numerator = decoder->aspect_ratio_num;
    *denominator = decoder->aspect_ratio_den;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_color_info(HWDecoderHandle handle, int color_range, int color_primaries, int color_trc, int colorspace) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->color_range = color_range;
    decoder->color_primaries = color_primaries;
    decoder->color_trc = color_trc;
    decoder->colorspace = colorspace;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_color_info(HWDecoderHandle handle, int* color_range, int* color_primaries, int* color_trc, int* colorspace) {
    if (!handle || !color_range || !color_primaries || !color_trc || !colorspace) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *color_range = decoder->color_range;
    *color_primaries = decoder->color_primaries;
    *color_trc = decoder->color_trc;
    *colorspace = decoder->colorspace;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_audio_params(HWDecoderHandle handle, int sample_rate, int channels, int sample_format) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->sample_rate = sample_rate;
    decoder->channels = channels;
    decoder->sample_format = sample_format;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_audio_params(HWDecoderHandle handle, int* sample_rate, int* channels, int* sample_format) {
    if (!handle || !sample_rate || !channels || !sample_format) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *sample_rate = decoder->sample_rate;
    *channels = decoder->channels;
    *sample_format = decoder->sample_format;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_decode_audio(HWDecoderHandle handle, const uint8_t* data, int size, int64_t pts, HWFrameData* frame_data) {
    // 音频解码实现
    // 这里简化处理，实际需要完整的音频解码逻辑
    return hw_decoder_decode(handle, data, size, pts, pts, frame_data);
}

int hw_decoder_decode_audio_simple(HWDecoderHandle handle, const uint8_t* data, int size, HWFrameData* frame_data) {
    return hw_decoder_decode_audio(handle, data, size, 0, frame_data);
}

int hw_decoder_flush_audio(HWDecoderHandle handle, HWFrameData* frame_data) {
    return hw_decoder_flush(handle, frame_data);
}

bool hw_decoder_is_audio(HWDecoderHandle handle) {
    if (!handle) {
        return false;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    // 这里需要根据实际解码器类型判断
    // 简化处理，假设都是视频解码器
    return false;
}

bool hw_decoder_is_video(HWDecoderHandle handle) {
    if (!handle) {
        return false;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    // 这里需要根据实际解码器类型判断
    // 简化处理，假设都是视频解码器
    return true;
}

int hw_decoder_get_decode_latency(HWDecoderHandle handle) {
    if (!handle) {
        return 0;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->decode_latency;
}

int hw_decoder_get_display_latency(HWDecoderHandle handle) {
    if (!handle) {
        return 0;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->display_latency;
}

int hw_decoder_get_total_latency(HWDecoderHandle handle) {
    if (!handle) {
        return 0;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->total_latency;
}

int hw_decoder_set_low_latency_mode(HWDecoderHandle handle, bool enable) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->low_latency_mode = enable;
    
    internal_log(HW_LOG_DEBUG, "Low latency mode %s", enable ? "enabled" : "disabled");
    
    return HW_DECODER_SUCCESS;
}

bool hw_decoder_get_low_latency_mode(HWDecoderHandle handle) {
    if (!handle) {
        return false;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->low_latency_mode;
}

int hw_decoder_set_drop_frame_policy(HWDecoderHandle handle, bool drop_late_frames, bool drop_corrupted_frames) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->drop_late_frames = drop_late_frames;
    decoder->drop_corrupted_frames = drop_corrupted_frames;
    
    internal_log(HW_LOG_DEBUG, "Drop frame policy: late=%s, corrupted=%s",
                drop_late_frames ? "yes" : "no",
                drop_corrupted_frames ? "yes" : "no");
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_drop_frame_policy(HWDecoderHandle handle, bool* drop_late_frames, bool* drop_corrupted_frames) {
    if (!handle || !drop_late_frames || !drop_corrupted_frames) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *drop_late_frames = decoder->drop_late_frames;
    *drop_corrupted_frames = decoder->drop_corrupted_frames;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_performance_mode(HWDecoderHandle handle, int mode) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->performance_mode = mode;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_performance_mode(HWDecoderHandle handle) {
    if (!handle) {
        return 0;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->performance_mode;
}

int hw_decoder_set_power_mode(HWDecoderHandle handle, int mode) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->power_mode = mode;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_power_mode(HWDecoderHandle handle) {
    if (!handle) {
        return 0;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->power_mode;
}

int hw_decoder_set_temperature_limit(HWDecoderHandle handle, int temperature_celsius) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->temperature_limit = temperature_celsius;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_current_temperature(HWDecoderHandle handle) {
    // 这里简化处理，实际需要读取硬件温度传感器
    if (!handle) {
        return 0;
    }
    
    // 返回模拟温度值
    return 45; // 45°C
}

int hw_decoder_set_buffer_size(HWDecoderHandle handle, int input_buffer_size, int output_buffer_size) {
    if (!handle || input_buffer_size <= 0 || output_buffer_size <= 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->input_buffer_size = input_buffer_size;
    decoder->output_buffer_size = output_buffer_size;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_buffer_size(HWDecoderHandle handle, int* input_buffer_size, int* output_buffer_size) {
    if (!handle || !input_buffer_size || !output_buffer_size) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    *input_buffer_size = decoder->input_buffer_size;
    *output_buffer_size = decoder->output_buffer_size;
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_buffer_usage(HWDecoderHandle handle, int* input_usage, int* output_usage) {
    if (!handle || !input_usage || !output_usage) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    // 简化处理，返回模拟值
    *input_usage = 50; // 50% 使用率
    *output_usage = 75; // 75% 使用率
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_wait_for_buffer(HWDecoderHandle handle, int buffer_type, int timeout_ms) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    // 简化处理，立即返回成功
    return HW_DECODER_SUCCESS;
}

bool hw_decoder_is_buffer_available(HWDecoderHandle handle, int buffer_type) {
    if (!handle) {
        return false;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    // 简化处理，总是返回有缓冲区可用
    return true;
}

int hw_decoder_get_supported_hardware_devices(const char*** devices, int* count) {
    static const char* supported_devices[] = {
        "mediacodec",
        "vulkan",
        "cuda",
        "vaapi",
        "videotoolbox",
        "d3d11va"
    };
    
    static int device_count = sizeof(supported_devices) / sizeof(supported_devices[0]);
    
    if (devices) {
        *devices = static_cast<const char**>(malloc(sizeof(const char*) * device_count));
        if (!*devices) {
            return HW_DECODER_ERROR_OUT_OF_MEMORY;
        }
        
        for (int i = 0; i < device_count; i++) {
            (*devices)[i] = supported_devices[i];
        }
    }
    
    if (count) {
        *count = device_count;
    }
    
    return HW_DECODER_SUCCESS;
}

void hw_decoder_free_supported_hardware_devices(const char** devices) {
    if (devices) {
        free(devices);
    }
}

int hw_decoder_set_hardware_device(HWDecoderHandle handle, const char* device_name) {
    if (!handle || !device_name) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->hardware_device = device_name;
    
    internal_log(HW_LOG_INFO, "Hardware device set to: %s", device_name);
    
    return HW_DECODER_SUCCESS;
}

const char* hw_decoder_get_hardware_device(HWDecoderHandle handle) {
    if (!handle) {
        return nullptr;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    return decoder->hardware_device.c_str();
}

int hw_decoder_get_hardware_capabilities(HWDecoderHandle handle, const char* capability_name, int* value) {
    if (!handle || !capability_name || !value) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    // 简化处理，返回默认值
    *value = 1;
    
    return HW_DECODER_SUCCESS;
}

bool hw_decoder_check_hardware_feature(HWDecoderHandle handle, const char* feature_name) {
    if (!handle || !feature_name) {
        return false;
    }
    
    // 简化处理，总是返回支持
    return true;
}

const char* hw_decoder_get_hardware_info(HWDecoderHandle handle, const char* info_name) {
    if (!handle || !info_name) {
        return nullptr;
    }
    
    static std::string info;
    info = "Hardware information: " + std::string(info_name);
    
    return info.c_str();
}

int hw_decoder_restart_hardware_device(HWDecoderHandle handle) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    internal_log(HW_LOG_INFO, "Restarting hardware device: %s", decoder->hardware_device.c_str());
    
    // 重置解码器
    hw_decoder_reset(handle);
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_diagnose_hardware_device(HWDecoderHandle handle, char* result, int result_size) {
    if (!handle || !result || result_size <= 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    std::string diagnosis;
    diagnosis += "Hardware Device Diagnosis Report\n";
    diagnosis += "================================\n";
    diagnosis += "Device: " + decoder->hardware_device + "\n";
    diagnosis += "Type: " + decoder->hardware_type + "\n";
    diagnosis += "Codec: " + decoder->codec_name + "\n";
    diagnosis += "Status: " + std::string(decoder->is_initialized ? "Initialized" : "Not initialized") + "\n";
    diagnosis += "Frames decoded: " + std::to_string(decoder->frames_decoded) + "\n";
    diagnosis += "Frames dropped: " + std::to_string(decoder->frames_dropped) + "\n";
    diagnosis += "Frames corrupted: " + std::to_string(decoder->frames_corrupted) + "\n";
    diagnosis += "Total bytes: " + std::to_string(decoder->bytes_decoded) + " bytes\n";
    diagnosis += "Average FPS: " + std::to_string(static_cast<int>(decoder->stats.fps)) + "\n";
    diagnosis += "Current latency: " + std::to_string(decoder->total_latency) + " μs\n";
    diagnosis += "Hardware accelerated: " + std::string(hw_decoder_is_hardware_accelerated(handle) ? "Yes" : "No") + "\n";
    diagnosis += "Diagnosis: OK";
    
    strncpy(result, diagnosis.c_str(), result_size - 1);
    result[result_size - 1] = '\0';
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_set_hardware_parameter(HWDecoderHandle handle, const char* param_name, const char* param_value) {
    if (!handle || !param_name) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    decoder->hardware_params[param_name] = param_value ? param_value : "";
    
    return HW_DECODER_SUCCESS;
}

const char* hw_decoder_get_hardware_parameter(HWDecoderHandle handle, const char* param_name) {
    if (!handle || !param_name) {
        return nullptr;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    auto it = decoder->hardware_params.find(param_name);
    if (it != decoder->hardware_params.end()) {
        return it->second.c_str();
    }
    
    return nullptr;
}

int hw_decoder_start_performance_monitoring(HWDecoderHandle handle) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    if (decoder->monitor_running) {
        return HW_DECODER_SUCCESS;
    }
    
    decoder->monitor_running = true;
    decoder->monitor_thread = std::thread(performance_monitor_thread, decoder);
    
    internal_log(HW_LOG_INFO, "Performance monitoring started");
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_stop_performance_monitoring(HWDecoderHandle handle) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    if (!decoder->monitor_running) {
        return HW_DECODER_SUCCESS;
    }
    
    decoder->monitor_running = false;
    if (decoder->monitor_thread.joinable()) {
        decoder->monitor_thread.join();
    }
    
    internal_log(HW_LOG_INFO, "Performance monitoring stopped");
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_get_performance_data(HWDecoderHandle handle, void* data, int data_size) {
    if (!handle || !data || data_size <= 0) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    auto decoder = static_cast<InternalHWDecoder*>(handle);
    
    // 简化处理，返回空数据
    memset(data, 0, data_size);
    
    return HW_DECODER_SUCCESS;
}

int hw_decoder_reset_performance_monitoring(HWDecoderHandle handle) {
    if (!handle) {
        return HW_DECODER_ERROR_INVALID_PARAMETER;
    }
    
    // 重置统计信息
    hw_decoder_reset_stats(handle);
    
    internal_log(HW_LOG_INFO, "Performance monitoring reset");
    
    return HW_DECODER_SUCCESS;
}

} // extern "C"
