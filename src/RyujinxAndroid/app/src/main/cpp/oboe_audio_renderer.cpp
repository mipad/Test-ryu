// oboe_audio_renderer.cpp (修复时序和同步问题)
#include "oboe_audio_renderer.h"
#include <cstring>
#include <algorithm>
#include <thread>
#include <chrono>
#include <cmath>
#include <array>
#include <limits>

namespace RyujinxOboe {

// =============== ADPCM 解码表 ===============
static const int16_t ADPCM_INDEX_TABLE[16] = {
    -1, -1, -1, -1, 2, 4, 6, 8,
    -1, -1, -1, -1, 2, 4, 6, 8
};

static const int16_t ADPCM_STEP_TABLE[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
    253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
    1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
    3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
    12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
};

// =============== 时序管理函数 ===============
void OboeAudioRenderer::UpdateTimingStats() {
    auto now = std::chrono::steady_clock::now();
    
    if (m_timing_stats.last_callback_time != std::chrono::steady_clock::time_point()) {
        auto interval = std::chrono::duration_cast<std::chrono::microseconds>(
            now - m_timing_stats.last_callback_time);
        double interval_ms = interval.count() / 1000.0;
        
        m_timing_stats.average_callback_interval_ms = 
            (m_timing_stats.average_callback_interval_ms * m_timing_stats.total_callbacks + interval_ms) 
            / (m_timing_stats.total_callbacks + 1);
        
        if (interval_ms > m_timing_stats.max_callback_interval_ms) {
            m_timing_stats.max_callback_interval_ms = interval_ms;
        }
        
        // 检查是否延迟（假设48kHz，240帧应该是5ms）
        if (interval_ms > 10.0) { // 允许2倍延迟
            m_timing_stats.late_callbacks++;
        }
    }
    
    m_timing_stats.last_callback_time = now;
    m_timing_stats.total_callbacks++;
}

bool OboeAudioRenderer::IsCallbackOnTime() const {
    return m_timing_stats.late_callbacks < m_timing_stats.total_callbacks / 10; // 少于10%的延迟
}

void OboeAudioRenderer::AdjustBufferForTiming() {
    auto now = std::chrono::steady_clock::now();
    auto time_since_adjustment = std::chrono::duration_cast<std::chrono::milliseconds>(
        now - m_last_buffer_adjustment);
    
    // 每500ms调整一次缓冲区
    if (time_since_adjustment.count() < 500) {
        return;
    }
    
    if (!m_stream) {
        return;
    }
    
    int32_t current_buffer_size = m_stream->getBufferSizeInFrames();
    int32_t desired_buffer_size = current_buffer_size;
    
    // 根据时序统计调整缓冲区大小
    if (!IsCallbackOnTime() && m_timing_stats.late_callbacks > 0) {
        // 增加缓冲区大小以减少欠载
        desired_buffer_size = std::min(current_buffer_size * 120 / 100, current_buffer_size + m_frames_per_burst.load());
        m_timing_adjustments++;
    } else if (m_timing_stats.late_callbacks == 0 && current_buffer_size > m_frames_per_burst.load() * 2) {
        // 减少缓冲区大小以降低延迟
        desired_buffer_size = std::max(m_frames_per_burst.load() * 2, current_buffer_size * 80 / 100);
        m_timing_adjustments++;
    }
    
    if (desired_buffer_size != current_buffer_size) {
        AdjustBufferSize(desired_buffer_size);
        m_last_buffer_adjustment = now;
    }
}

// =============== 下混函数实现 ===============
bool OboeAudioRenderer::DownmixSurroundToStereo(const uint8_t* input, uint8_t* output, size_t frames, int32_t input_format) {
    if (!input || !output || frames == 0) return false;
    
    // 目前只支持PCM16格式的下混
    if (input_format != PCM_INT16) {
        return false;
    }
    
    const int16_t* in_samples = reinterpret_cast<const int16_t*>(input);
    int16_t* out_samples = reinterpret_cast<int16_t*>(output);
    
    // 6声道布局: FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight
    for (size_t i = 0; i < frames; i++) {
        size_t base_idx = i * 6;
        
        int16_t front_left = in_samples[base_idx];
        int16_t front_right = in_samples[base_idx + 1];
        int16_t front_center = in_samples[base_idx + 2];
        int16_t low_freq = in_samples[base_idx + 3];
        int16_t back_left = in_samples[base_idx + 4];
        int16_t back_right = in_samples[base_idx + 5];
        
        // 应用下混系数 (与C# Downmixing.cs保持一致)
        int32_t left_mix = 
            (front_left * SURROUND_TO_STEREO_COEFFS[0]) +  // 前左
            (back_left * SURROUND_TO_STEREO_COEFFS[3]) +   // 后左  
            (low_freq * SURROUND_TO_STEREO_COEFFS[2]) +    // 低频
            (front_center * SURROUND_TO_STEREO_COEFFS[1]); // 中置
        
        int32_t right_mix = 
            (front_right * SURROUND_TO_STEREO_COEFFS[0]) + // 前右
            (back_right * SURROUND_TO_STEREO_COEFFS[3]) +  // 后右
            (low_freq * SURROUND_TO_STEREO_COEFFS[2]) +    // 低频
            (front_center * SURROUND_TO_STEREO_COEFFS[1]); // 中置
        
        // 应用Q15缩放并限制范围
        left_mix = (left_mix + RAW_Q15_HALF_ONE) >> Q15_BITS;
        right_mix = (right_mix + RAW_Q15_HALF_ONE) >> Q15_BITS;
        
        out_samples[i * 2] = static_cast<int16_t>(std::max<int32_t>(-32768, std::min<int32_t>(32767, left_mix)));
        out_samples[i * 2 + 1] = static_cast<int16_t>(std::max<int32_t>(-32768, std::min<int32_t>(32767, right_mix)));
    }
    
    return true;
}

bool OboeAudioRenderer::DownmixStereoToMono(const uint8_t* input, uint8_t* output, size_t frames, int32_t input_format) {
    if (!input || !output || frames == 0) return false;
    
    // 目前只支持PCM16格式的下混
    if (input_format != PCM_INT16) {
        return false;
    }
    
    const int16_t* in_samples = reinterpret_cast<const int16_t*>(input);
    int16_t* out_samples = reinterpret_cast<int16_t*>(output);
    
    for (size_t i = 0; i < frames; i++) {
        int16_t left = in_samples[i * 2];
        int16_t right = in_samples[i * 2 + 1];
        
        // 应用下混系数
        int32_t mono_mix = 
            (left * STEREO_TO_MONO_COEFFS[0]) + 
            (right * STEREO_TO_MONO_COEFFS[1]);
        
        // 应用Q15缩放并限制范围
        mono_mix = mono_mix >> Q15_BITS;
        out_samples[i] = static_cast<int16_t>(std::max<int32_t>(-32768, std::min<int32_t>(32767, mono_mix)));
    }
    
    return true;
}

// =============== 格式转换函数实现 ===============
bool OboeAudioRenderer::ConvertPCM8ToPCM16(const uint8_t* input, int16_t* output, size_t samples) {
    if (!input || !output) return false;
    
    for (size_t i = 0; i < samples; i++) {
        // PCM8: 0-255 to PCM16: -32768 to 32767
        output[i] = static_cast<int16_t>((static_cast<int32_t>(input[i]) - 128) * 256);
    }
    return true;
}

bool OboeAudioRenderer::ConvertPCM16ToPCM8(const int16_t* input, uint8_t* output, size_t samples) {
    if (!input || !output) return false;
    
    for (size_t i = 0; i < samples; i++) {
        // PCM16: -32768 to 32767 to PCM8: 0-255
        int32_t sample = (static_cast<int32_t>(input[i]) / 256) + 128;
        output[i] = static_cast<uint8_t>(std::max<int>(0, std::min<int>(255, sample)));
    }
    return true;
}

bool OboeAudioRenderer::ConvertPCM24ToPCM32(const uint8_t* input, int32_t* output, size_t samples) {
    if (!input || !output) return false;
    
    for (size_t i = 0; i < samples; i++) {
        const uint8_t* sample_ptr = input + (i * 3);
        int32_t sample;
        
        // 处理符号扩展
        if (sample_ptr[2] & 0x80) {
            // 负数
            sample = (0xFF << 24) | (sample_ptr[2] << 16) | (sample_ptr[1] << 8) | sample_ptr[0];
        } else {
            // 正数
            sample = (sample_ptr[2] << 16) | (sample_ptr[1] << 8) | sample_ptr[0];
        }
        
        output[i] = sample;
    }
    return true;
}

bool OboeAudioRenderer::ConvertPCM32ToPCM24(const int32_t* input, uint8_t* output, size_t samples) {
    if (!input || !output) return false;
    
    for (size_t i = 0; i < samples; i++) {
        uint8_t* sample_ptr = output + (i * 3);
        int32_t sample = input[i];
        
        sample_ptr[0] = static_cast<uint8_t>(sample & 0xFF);
        sample_ptr[1] = static_cast<uint8_t>((sample >> 8) & 0xFF);
        sample_ptr[2] = static_cast<uint8_t>((sample >> 16) & 0xFF);
    }
    return true;
}

bool OboeAudioRenderer::ConvertPCM16ToFloat(const int16_t* input, float* output, size_t samples) {
    if (!input || !output) return false;
    
    const float scale = 1.0f / 32768.0f;
    for (size_t i = 0; i < samples; i++) {
        output[i] = static_cast<float>(input[i]) * scale;
    }
    return true;
}

bool OboeAudioRenderer::ConvertFloatToPCM16(const float* input, int16_t* output, size_t samples) {
    if (!input || !output) return false;
    
    for (size_t i = 0; i < samples; i++) {
        float sample = std::max(-1.0f, std::min(1.0f, input[i]));
        output[i] = static_cast<int16_t>(sample * 32767.0f);
    }
    return true;
}

bool OboeAudioRenderer::ConvertPCM32ToFloat(const int32_t* input, float* output, size_t samples) {
    if (!input || !output) return false;
    
    const float scale = 1.0f / 2147483648.0f;
    for (size_t i = 0; i < samples; i++) {
        output[i] = static_cast<float>(input[i]) * scale;
    }
    return true;
}

bool OboeAudioRenderer::ConvertFloatToPCM32(const float* input, int32_t* output, size_t samples) {
    if (!input || !output) return false;
    
    for (size_t i = 0; i < samples; i++) {
        float sample = std::max(-1.0f, std::min(1.0f, input[i]));
        output[i] = static_cast<int32_t>(sample * 2147483647.0f);
    }
    return true;
}

// =============== ADPCM 解码实现 ===============
int16_t OboeAudioRenderer::DecodeADPCMSample(uint8_t nibble, ADPCMState& state) {
    int16_t diff = state.step >> 3;
    
    if (nibble & 1) diff += state.step >> 2;
    if (nibble & 2) diff += state.step >> 1;
    if (nibble & 4) diff += state.step;
    if (nibble & 8) diff = -diff;
    
    state.predictor += diff;
    state.predictor = static_cast<int16_t>(std::max<int>(-32768, std::min<int>(32767, state.predictor)));
    
    state.step_index += ADPCM_INDEX_TABLE[nibble];
    state.step_index = static_cast<int8_t>(std::max<int>(0, std::min<int>(88, state.step_index)));
    state.step = ADPCM_STEP_TABLE[state.step_index];
    
    return state.predictor;
}

bool OboeAudioRenderer::DecodeADPCM(const uint8_t* input, size_t input_size, int16_t* output, 
                                   size_t output_samples, ADPCMState& state, int32_t channels) {
    if (!input || !output || input_size == 0 || output_samples == 0) return false;
    
    size_t samples_decoded = 0;
    size_t input_pos = 0;
    
    while (input_pos < input_size && samples_decoded < output_samples) {
        uint8_t byte = input[input_pos++];
        
        // 解码高4位
        uint8_t high_nibble = (byte >> 4) & 0x0F;
        output[samples_decoded++] = DecodeADPCMSample(high_nibble, state);
        
        if (samples_decoded >= output_samples) break;
        
        // 解码低4位
        uint8_t low_nibble = byte & 0x0F;
        output[samples_decoded++] = DecodeADPCMSample(low_nibble, state);
    }
    
    return true;
}

// =============== 声道转换函数 ===============
bool OboeAudioRenderer::ConvertChannels(const uint8_t* input, uint8_t* output, size_t frames, 
                                       int32_t input_format, int32_t input_channels, int32_t output_channels) {
    if (!input || !output || frames == 0) return false;
    
    if (input_channels == output_channels) {
        // 声道数相同，直接复制
        size_t bytes_per_frame = GetBytesPerSample(input_format) * input_channels;
        std::memcpy(output, input, frames * bytes_per_frame);
        return true;
    }
    
    size_t bytes_per_sample = GetBytesPerSample(input_format);
    
    // 简化实现：只处理常见的声道转换
    if (input_channels == 1 && output_channels == 2) {
        // 单声道转立体声：复制单声道数据到左右声道
        for (size_t i = 0; i < frames; i++) {
            const uint8_t* in_sample = input + (i * bytes_per_sample);
            uint8_t* out_left = output + (i * 2 * bytes_per_sample);
            uint8_t* out_right = out_left + bytes_per_sample;
            
            std::memcpy(out_left, in_sample, bytes_per_sample);
            std::memcpy(out_right, in_sample, bytes_per_sample);
        }
        return true;
    } else if (input_channels == 2 && output_channels == 1) {
        // 立体声转单声道：左右声道平均
        for (size_t i = 0; i < frames; i++) {
            const uint8_t* in_left = input + (i * 2 * bytes_per_sample);
            const uint8_t* in_right = in_left + bytes_per_sample;
            uint8_t* out_sample = output + (i * bytes_per_sample);
            
            // 简化处理：对于非浮点格式，直接复制左声道
            std::memcpy(out_sample, in_left, bytes_per_sample);
        }
        return true;
    }
    
    return false;
}

// =============== 通用格式转换函数 ===============
bool OboeAudioRenderer::ConvertFormat(const uint8_t* input, uint8_t* output, size_t frames, 
                                     int32_t input_format, int32_t output_format, int32_t input_channels, int32_t output_channels) {
    if (!input || !output || frames == 0) return false;
    
    // 如果格式和声道数都相同，直接复制
    if (input_format == output_format && input_channels == output_channels) {
        size_t bytes_per_frame = GetBytesPerSample(input_format) * input_channels;
        std::memcpy(output, input, frames * bytes_per_frame);
        return true;
    }
    
    size_t input_samples = frames * input_channels;
    size_t output_samples = frames * output_channels;
    
    // 首先进行声道转换（如果需要）
    std::vector<uint8_t> channel_converted_data;
    const uint8_t* process_data = input;
    
    if (input_channels != output_channels) {
        size_t channel_bytes = frames * output_channels * GetBytesPerSample(input_format);
        channel_converted_data.resize(channel_bytes);
        
        if (!ConvertChannels(input, channel_converted_data.data(), frames, input_format, input_channels, output_channels)) {
            return false;
        }
        
        process_data = channel_converted_data.data();
        input_samples = output_samples; // 更新样本数
    }
    
    // 然后进行格式转换（如果需要）
    if (input_format != output_format) {
        // 根据具体的格式组合调用相应的转换函数
        switch (input_format) {
            case PCM_INT8:
                switch (output_format) {
                    case PCM_INT16:
                        return ConvertPCM8ToPCM16(process_data, reinterpret_cast<int16_t*>(output), input_samples);
                    case PCM_INT32:
                    case PCM_FLOAT:
                        // 需要中间转换
                        break;
                    default:
                        break;
                }
                break;
                
            case PCM_INT16:
                switch (output_format) {
                    case PCM_INT8:
                        return ConvertPCM16ToPCM8(reinterpret_cast<const int16_t*>(process_data), output, input_samples);
                    case PCM_FLOAT:
                        return ConvertPCM16ToFloat(reinterpret_cast<const int16_t*>(process_data), reinterpret_cast<float*>(output), input_samples);
                    default:
                        break;
                }
                break;
                
            case PCM_INT24:
                switch (output_format) {
                    case PCM_INT32:
                        return ConvertPCM24ToPCM32(process_data, reinterpret_cast<int32_t*>(output), input_samples);
                    default:
                        break;
                }
                break;
                
            case PCM_INT32:
                switch (output_format) {
                    case PCM_INT24:
                        return ConvertPCM32ToPCM24(reinterpret_cast<const int32_t*>(process_data), output, input_samples);
                    case PCM_FLOAT:
                        return ConvertPCM32ToFloat(reinterpret_cast<const int32_t*>(process_data), reinterpret_cast<float*>(output), input_samples);
                    default:
                        break;
                }
                break;
                
            case PCM_FLOAT:
                switch (output_format) {
                    case PCM_INT16:
                        return ConvertFloatToPCM16(reinterpret_cast<const float*>(process_data), reinterpret_cast<int16_t*>(output), input_samples);
                    case PCM_INT32:
                        return ConvertFloatToPCM32(reinterpret_cast<const float*>(process_data), reinterpret_cast<int32_t*>(output), input_samples);
                    default:
                        break;
                }
                break;
                
            default:
                break;
        }
        
        // 如果不支持直接转换，尝试通过中间格式转换
        return false;
    } else {
        // 只有声道转换，没有格式转换
        if (process_data != input) {
            std::memcpy(output, process_data, frames * output_channels * GetBytesPerSample(output_format));
        }
        return true;
    }
}

// =============== 音量应用函数 ===============
void OboeAudioRenderer::ApplyVolume(void* data, size_t frames, int32_t format, int32_t channels, float volume) {
    if (volume == 1.0f || !data || frames == 0 || channels == 0) return;
    
    size_t total_samples = frames * channels;
    
    switch (format) {
        case PCM_INT8: {
            int8_t* samples = static_cast<int8_t*>(data);
            for (size_t i = 0; i < total_samples; i++) {
                int32_t sample = static_cast<int32_t>(samples[i]);
                sample = static_cast<int32_t>((sample - 128) * volume) + 128;
                samples[i] = static_cast<int8_t>(std::max<int>(-128, std::min<int>(127, sample)));
            }
            break;
        }
        case PCM_INT16: {
            int16_t* samples = static_cast<int16_t*>(data);
            for (size_t i = 0; i < total_samples; i++) {
                samples[i] = static_cast<int16_t>(samples[i] * volume);
            }
            break;
        }
        case PCM_INT32: {
            int32_t* samples = static_cast<int32_t*>(data);
            for (size_t i = 0; i < total_samples; i++) {
                samples[i] = static_cast<int32_t>(static_cast<int64_t>(samples[i]) * volume);
            }
            break;
        }
        case PCM_FLOAT: {
            float* samples = static_cast<float*>(data);
            for (size_t i = 0; i < total_samples; i++) {
                samples[i] *= volume;
            }
            break;
        }
        default:
            break;
    }
}

// =============== RawSampleBufferQueue Implementation ===============
bool OboeAudioRenderer::RawSampleBufferQueue::WriteRaw(const uint8_t* data, size_t data_size, int32_t sample_format, uint64_t session_id) {
    if (!data || data_size == 0) return false;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    // 检查队列是否已满
    if (m_buffers.size() >= m_max_buffers || m_current_total_size + data_size > m_max_total_size) {
        return false;
    }
    
    // 创建新原始数据缓冲区
    RawSampleBuffer buffer;
    buffer.data.resize(data_size);
    std::memcpy(buffer.data.data(), data, data_size);
    buffer.data_size = data_size;
    buffer.data_played = 0;
    buffer.sample_format = sample_format;
    buffer.consumed = false;
    buffer.session_id = session_id;
    buffer.queue_time = std::chrono::steady_clock::now();
    
    m_buffers.push(std::move(buffer));
    m_current_format = sample_format;
    m_current_total_size += data_size;
    
    return true;
}

size_t OboeAudioRenderer::RawSampleBufferQueue::ReadRaw(uint8_t* output, size_t output_size, int32_t target_format, int32_t target_channels) {
    if (!output || output_size == 0) return 0;
    
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t bytes_written = 0;
    std::vector<uint8_t> temp_buffer;
    
    while (bytes_written < output_size) {
        // 如果当前播放缓冲区已消费或为空，从队列获取新缓冲区
        if (m_playing_buffer.consumed || m_playing_buffer.data.empty()) {
            if (m_buffers.empty()) {
                break; // 没有更多数据
            }
            
            m_playing_buffer = std::move(m_buffers.front());
            m_buffers.pop();
            m_current_total_size -= m_playing_buffer.data_size;
        }
        
        // 计算当前缓冲区可用的数据
        size_t bytes_available = m_playing_buffer.data_size - m_playing_buffer.data_played;
        
        // 如果需要格式转换
        if (m_playing_buffer.sample_format != target_format) {
            // 计算可转换的帧数
            size_t input_bytes_per_frame = GetBytesPerSample(m_playing_buffer.sample_format) * target_channels;
            size_t output_bytes_per_frame = GetBytesPerSample(target_format) * target_channels;
            
            size_t available_frames = bytes_available / input_bytes_per_frame;
            size_t needed_frames = (output_size - bytes_written) / output_bytes_per_frame;
            size_t frames_to_process = std::min(available_frames, needed_frames);
            
            if (frames_to_process > 0) {
                size_t input_bytes_needed = frames_to_process * input_bytes_per_frame;
                size_t output_bytes_needed = frames_to_process * output_bytes_per_frame;
                
                const uint8_t* input_data = m_playing_buffer.data.data() + m_playing_buffer.data_played;
                
                // 执行格式转换
                if (ConvertFormat(input_data, output + bytes_written, frames_to_process, 
                                 m_playing_buffer.sample_format, target_format, target_channels, target_channels)) {
                    bytes_written += output_bytes_needed;
                    m_playing_buffer.data_played += input_bytes_needed;
                } else {
                    // 转换失败，跳过这些数据
                    m_playing_buffer.data_played += input_bytes_needed;
                }
            }
        } else {
            // 不需要格式转换，直接复制
            size_t bytes_to_copy = std::min(bytes_available, output_size - bytes_written);
            std::memcpy(output + bytes_written, 
                       m_playing_buffer.data.data() + m_playing_buffer.data_played,
                       bytes_to_copy);
            
            bytes_written += bytes_to_copy;
            m_playing_buffer.data_played += bytes_to_copy;
        }
        
        // 检查当前缓冲区是否已完全消费
        if (m_playing_buffer.data_played >= m_playing_buffer.data_size) {
            m_playing_buffer.consumed = true;
        }
    }
    
    return bytes_written;
}

size_t OboeAudioRenderer::RawSampleBufferQueue::Available() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    size_t total_bytes = 0;
    
    // 计算队列中所有缓冲区的总字节数
    std::queue<RawSampleBuffer> temp_queue = m_buffers;
    while (!temp_queue.empty()) {
        const auto& buffer = temp_queue.front();
        total_bytes += buffer.data_size;
        temp_queue.pop();
    }
    
    // 加上当前播放缓冲区剩余的字节数
    if (!m_playing_buffer.consumed && !m_playing_buffer.data.empty()) {
        total_bytes += (m_playing_buffer.data_size - m_playing_buffer.data_played);
    }
    
    return total_bytes;
}

size_t OboeAudioRenderer::RawSampleBufferQueue::GetMemoryUsage() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_current_total_size;
}

bool OboeAudioRenderer::RawSampleBufferQueue::IsEmpty() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_buffers.empty() && (m_playing_buffer.consumed || m_playing_buffer.data.empty());
}

size_t OboeAudioRenderer::RawSampleBufferQueue::GetBufferCount() const {
    std::lock_guard<std::mutex> lock(m_mutex);
    return m_buffers.size();
}

void OboeAudioRenderer::RawSampleBufferQueue::Clear() {
    std::lock_guard<std::mutex> lock(m_mutex);
    
    while (!m_buffers.empty()) {
        m_buffers.pop();
    }
    
    m_playing_buffer = RawSampleBuffer{};
    m_current_total_size = 0;
}

// =============== Audio Callback Implementation ===============
oboe::DataCallbackResult OboeAudioRenderer::AAudioExclusiveCallback::onAudioReady(
    oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    
    return m_renderer->OnAudioReadyMultiFormat(audioStream, audioData, num_frames);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorAfterClose(audioStream, error);
}

void OboeAudioRenderer::AAudioExclusiveErrorCallback::onErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    m_renderer->OnStreamErrorBeforeClose(audioStream, error);
}

// =============== OboeAudioRenderer Implementation ===============
OboeAudioRenderer::OboeAudioRenderer() {
    m_audio_callback = std::make_unique<AAudioExclusiveCallback>(this);
    m_error_callback = std::make_unique<AAudioExclusiveErrorCallback>(this);
    
    // 初始化ADPCM状态
    m_adpcm_state.predictor = 0;
    m_adpcm_state.step_index = 0;
    m_adpcm_state.step = ADPCM_STEP_TABLE[0];
    
    // 初始化时序统计
    m_timing_stats = TimingStats{};
    m_last_buffer_adjustment = std::chrono::steady_clock::now();
}

OboeAudioRenderer::~OboeAudioRenderer() {
    Shutdown();
}

OboeAudioRenderer& OboeAudioRenderer::GetInstance() {
    static OboeAudioRenderer instance;
    return instance;
}

bool OboeAudioRenderer::Initialize(int32_t sampleRate, int32_t channelCount) {
    return InitializeWithFormat(sampleRate, channelCount, PCM_INT16);
}

bool OboeAudioRenderer::InitializeWithFormat(int32_t sampleRate, int32_t channelCount, int32_t sampleFormat) {
    if (m_initialized.load()) {
        if (m_sample_rate.load() != sampleRate || 
            m_channel_count.load() != channelCount ||
            m_sample_format.load() != sampleFormat) {
            Shutdown();
        } else {
            return true;
        }
    }

    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    m_sample_rate.store(sampleRate);
    m_channel_count.store(channelCount);
    m_sample_format.store(sampleFormat);
    m_oboe_format = MapSampleFormat(sampleFormat);
    m_current_sample_format = GetFormatName(sampleFormat);
    
    // 使用原始格式样本缓冲区队列
    m_raw_sample_queue = std::make_unique<RawSampleBufferQueue>(64);
    
    if (!ConfigureAndOpenStream()) {
        return false;
    }
    
    m_initialized.store(true);
    return true;
}

void OboeAudioRenderer::Shutdown() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    CloseStream();
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
        m_raw_sample_queue.reset();
    }
    
    m_initialized.store(false);
    m_stream_started.store(false);
}

void OboeAudioRenderer::ConfigureForAAudioExclusive(oboe::AudioStreamBuilder& builder) {
    // AAudio 独占模式配置
    builder.setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setAudioApi(oboe::AudioApi::AAudio)
           ->setSharingMode(oboe::SharingMode::Exclusive)
           ->setDirection(oboe::Direction::Output)
           ->setSampleRate(m_sample_rate.load())
           ->setSampleRateConversionQuality(oboe::SampleRateConversionQuality::Medium)
           ->setFormat(m_oboe_format)
           ->setFormatConversionAllowed(true)
           ->setUsage(oboe::Usage::Game);
    
    // 设置固定的回调帧数
    builder.setFramesPerCallback(240);
    
    // 设置声道配置
    auto channel_count = m_channel_count.load();
    auto channel_mask = [&]() {
        switch (channel_count) {
        case 1:
            return oboe::ChannelMask::Mono;
        case 2:
            return oboe::ChannelMask::Stereo;
        case 6:
            return oboe::ChannelMask::CM5Point1;
        default:
            return oboe::ChannelMask::Unspecified;
        }
    }();
    
    builder.setChannelCount(channel_count)
           ->setChannelMask(channel_mask)
           ->setChannelConversionAllowed(true);
}

bool OboeAudioRenderer::ConfigureAndOpenStream() {
    oboe::AudioStreamBuilder builder;
    
    ConfigureForAAudioExclusive(builder);
    builder.setDataCallback(m_audio_callback.get())
           ->setErrorCallback(m_error_callback.get());
    
    // 尝试AAudio独占模式
    auto result = builder.openStream(m_stream);
    
    if (result != oboe::Result::OK) {
        // 回退到AAudio共享模式
        builder.setSharingMode(oboe::SharingMode::Shared);
        result = builder.openStream(m_stream);
        
        if (result != oboe::Result::OK) {
            // 最终回退到OpenSLES
            builder.setAudioApi(oboe::AudioApi::OpenSLES)
                   ->setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(m_stream);
            
            if (result != oboe::Result::OK) {
                return false;
            } else {
                m_current_audio_api = "OpenSLES";
                m_current_sharing_mode = "Shared";
            }
        } else {
            m_current_audio_api = "AAudio";
            m_current_sharing_mode = "Shared";
        }
    } else {
        m_current_audio_api = "AAudio";
        m_current_sharing_mode = "Exclusive";
    }
    
    // 优化缓冲区大小
    if (!OptimizeBufferSize()) {
        CloseStream();
        return false;
    }
    
    m_device_channels = m_stream->getChannelCount();
    m_device_sample_rate = m_stream->getSampleRate();
    
    // 启动流
    result = m_stream->requestStart();
    if (result != oboe::Result::OK) {
        CloseStream();
        return false;
    }
    
    m_stream_started.store(true);
    return true;
}

bool OboeAudioRenderer::OptimizeBufferSize() {
    if (!m_stream) {
        return false;
    }
    
    int32_t framesPerBurst = m_stream->getFramesPerBurst();
    int32_t desired_buffer_size;
    
    if (framesPerBurst > 0) {
        // 使用 FramesPerBurst * 4 作为缓冲区大小（更稳定的配置）
        desired_buffer_size = framesPerBurst * 4;
    } else {
        // 无法获取 FramesPerBurst，使用固定值
        desired_buffer_size = 1920; // 240 * 8
    }
    
    return AdjustBufferSize(desired_buffer_size);
}

bool OboeAudioRenderer::AdjustBufferSize(int32_t desired_size) {
    if (!m_stream) {
        return false;
    }
    
    auto setBufferResult = m_stream->setBufferSizeInFrames(desired_size);
    
    // 记录实际的缓冲区大小
    int32_t actual_buffer_size = m_stream->getBufferSizeInFrames();
    
    m_frames_per_burst.store(m_stream->getFramesPerBurst());
    m_buffer_size.store(actual_buffer_size);
    
    return setBufferResult == oboe::Result::OK;
}

bool OboeAudioRenderer::OpenStream() {
    return ConfigureAndOpenStream();
}

void OboeAudioRenderer::CloseStream() {
    if (m_stream) {
        if (m_stream_started.load()) {
            m_stream->stop();
        }
        m_stream->close();
        m_stream.reset();
        m_stream_started.store(false);
    }
}

bool OboeAudioRenderer::WriteAudio(const int16_t* data, int32_t num_frames) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    // 计算总样本数
    int32_t system_channels = m_channel_count.load();
    size_t total_samples = num_frames * system_channels;
    size_t data_size = total_samples * sizeof(int16_t);
    
    // 转换为原始格式写入
    return WriteAudioRaw(reinterpret_cast<const void*>(data), num_frames, PCM_INT16);
}

bool OboeAudioRenderer::WriteAudioRaw(const void* data, int32_t num_frames, int32_t sampleFormat) {
    if (!m_initialized.load() || !data || num_frames <= 0) {
        return false;
    }
    
    if (!m_raw_sample_queue) {
        return false;
    }
    
    // 检查缓冲区是否过载
    size_t current_buffered_ms = (GetBufferedFrames() * 1000) / m_sample_rate.load();
    if (current_buffered_ms > MAX_BUFFERED_MS) {
        m_buffer_overrun_count++;
        // 缓冲区过载，丢弃一些数据
        if (m_raw_sample_queue->GetBufferCount() > 8) {
            m_raw_sample_queue->Clear();
        }
    }
    
    // 处理ADPCM格式
    if (sampleFormat == ADPCM) {
        // ADPCM需要解码为PCM16
        int32_t system_channels = m_channel_count.load();
        size_t output_samples = num_frames * system_channels;
        size_t output_size = output_samples * sizeof(int16_t);
        
        std::vector<int16_t> pcm_data(output_samples);
        const uint8_t* adpcm_data = static_cast<const uint8_t*>(data);
        size_t adpcm_data_size = num_frames * system_channels / 2; // ADPCM是4:1压缩
        
        if (DecodeADPCM(adpcm_data, adpcm_data_size, pcm_data.data(), output_samples, m_adpcm_state, system_channels)) {
            m_adpcm_decoded_count++;
            return m_raw_sample_queue->WriteRaw(reinterpret_cast<const uint8_t*>(pcm_data.data()), 
                                              output_size, PCM_INT16, 0);
        } else {
            return false;
        }
    }
    
    // 计算数据大小
    int32_t system_channels = m_channel_count.load();
    size_t bytes_per_sample = GetBytesPerSample(sampleFormat);
    size_t data_size = num_frames * system_channels * bytes_per_sample;
    
    const uint8_t* input_data = static_cast<const uint8_t*>(data);
    
    // 检查是否需要下混
    bool needs_downmixing = false;
    int32_t target_channels = system_channels;
    
    if (system_channels == 6 && m_device_channels == 2) {
        // 6声道 -> 立体声下混
        needs_downmixing = true;
        target_channels = 2;
    } else if (system_channels == 2 && m_device_channels == 1) {
        // 立体声 -> 单声道下混  
        needs_downmixing = true;
        target_channels = 1;
    }
    
    bool success = false;
    
    if (needs_downmixing) {
        // 执行下混
        size_t output_data_size = num_frames * target_channels * bytes_per_sample;
        std::vector<uint8_t> downmixed_data(output_data_size);
        
        bool downmix_result = false;
        if (system_channels == 6 && target_channels == 2) {
            downmix_result = DownmixSurroundToStereo(input_data, downmixed_data.data(), num_frames, sampleFormat);
        } else if (system_channels == 2 && target_channels == 1) {
            downmix_result = DownmixStereoToMono(input_data, downmixed_data.data(), num_frames, sampleFormat);
        }
        
        if (downmix_result) {
            success = m_raw_sample_queue->WriteRaw(downmixed_data.data(), output_data_size, sampleFormat, 0);
        } else {
            // 下混失败，回退到直接写入
            success = m_raw_sample_queue->WriteRaw(input_data, data_size, sampleFormat, 0);
        }
    } else {
        // 不需要下混，直接写入
        success = m_raw_sample_queue->WriteRaw(input_data, data_size, sampleFormat, 0);
    }
    
    if (success) {
        m_frames_written += num_frames;
    } else {
        m_underrun_count++;
    }
    
    return success;
}

int32_t OboeAudioRenderer::GetBufferedFrames() const {
    if (!m_raw_sample_queue) return 0;
    
    size_t total_bytes = m_raw_sample_queue->Available();
    int32_t device_channels = m_device_channels;
    int32_t current_format = m_raw_sample_queue->GetCurrentFormat();
    size_t bytes_per_sample = GetBytesPerSample(current_format);
    
    if (device_channels == 0 || bytes_per_sample == 0) {
        return 0;
    }
    
    // 将字节数转换为帧数
    return static_cast<int32_t>(total_bytes / (device_channels * bytes_per_sample));
}

void OboeAudioRenderer::SetVolume(float volume) {
    m_volume.store(std::clamp(volume, 0.0f, 1.0f));
}

void OboeAudioRenderer::Reset() {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_raw_sample_queue) {
        m_raw_sample_queue->Clear();
    }
    
    // 重置ADPCM状态
    m_adpcm_state.predictor = 0;
    m_adpcm_state.step_index = 0;
    m_adpcm_state.step = ADPCM_STEP_TABLE[0];
    
    // 重置时序统计
    m_timing_stats = TimingStats{};
    m_last_buffer_adjustment = std::chrono::steady_clock::now();
    
    CloseStream();
    ConfigureAndOpenStream();
    
    m_stream_restart_count++;
}

oboe::AudioFormat OboeAudioRenderer::MapSampleFormat(int32_t format) {
    switch (format) {
        case PCM_INT8:  return oboe::AudioFormat::I16;  // PCM8需要转换为PCM16
        case PCM_INT16: return oboe::AudioFormat::I16;
        case PCM_INT24: return oboe::AudioFormat::I24;
        case PCM_INT32: return oboe::AudioFormat::I32;
        case PCM_FLOAT: return oboe::AudioFormat::Float;
        case ADPCM:     return oboe::AudioFormat::I16;  // ADPCM解码为PCM16
        default:        return oboe::AudioFormat::I16;
    }
}

const char* OboeAudioRenderer::GetFormatName(int32_t format) {
    switch (format) {
        case INVALID:   return "Invalid";
        case PCM_INT8:  return "PCM8";
        case PCM_INT16: return "PCM16";
        case PCM_INT24: return "PCM24";
        case PCM_INT32: return "PCM32";
        case PCM_FLOAT: return "Float32";
        case ADPCM:     return "ADPCM";
        default:        return "Unknown";
    }
}

size_t OboeAudioRenderer::GetBytesPerSample(int32_t format) {
    switch (format) {
        case PCM_INT8:  return 1;
        case PCM_INT16: return 2;
        case PCM_INT24: return 3;
        case PCM_INT32: return 4;
        case PCM_FLOAT: return 4;
        case ADPCM:     return 2; // ADPCM解码后为PCM16
        default:        return 2;
    }
}

bool OboeAudioRenderer::IsFormatSupported(int32_t format) {
    switch (format) {
        case PCM_INT8:
        case PCM_INT16:
        case PCM_INT24:
        case PCM_INT32:
        case PCM_FLOAT:
        case ADPCM:
            return true;
        default:
            return false;
    }
}

bool OboeAudioRenderer::NeedsResampling() const {
    return m_sample_rate.load() != m_device_sample_rate;
}

bool OboeAudioRenderer::ResampleAudio(const uint8_t* input, size_t input_frames, uint8_t* output, size_t output_frames, int32_t format) {
    // 简化重采样实现：线性插值
    if (!input || !output || input_frames == 0 || output_frames == 0) return false;
    
    if (input_frames == output_frames) {
        // 不需要重采样
        size_t bytes_per_frame = GetBytesPerSample(format) * m_channel_count.load();
        std::memcpy(output, input, input_frames * bytes_per_frame);
        return true;
    }
    
    // 这里实现一个简单的线性重采样
    // 实际项目中应该使用更高质量的重采样算法
    double ratio = static_cast<double>(input_frames) / output_frames;
    
    switch (format) {
        case PCM_INT16: {
            const int16_t* in = reinterpret_cast<const int16_t*>(input);
            int16_t* out = reinterpret_cast<int16_t*>(output);
            int32_t channels = m_channel_count.load();
            
            for (size_t i = 0; i < output_frames; i++) {
                double src_pos = i * ratio;
                size_t src_index = static_cast<size_t>(src_pos);
                double frac = src_pos - src_index;
                
                for (int32_t ch = 0; ch < channels; ch++) {
                    if (src_index < input_frames - 1) {
                        int16_t sample1 = in[src_index * channels + ch];
                        int16_t sample2 = in[(src_index + 1) * channels + ch];
                        out[i * channels + ch] = static_cast<int16_t>(sample1 + frac * (sample2 - sample1));
                    } else {
                        out[i * channels + ch] = in[src_index * channels + ch];
                    }
                }
            }
            return true;
        }
        // 其他格式的重采样实现...
        default:
            return false;
    }
}

oboe::DataCallbackResult OboeAudioRenderer::OnAudioReadyMultiFormat(oboe::AudioStream* audioStream, void* audioData, int32_t num_frames) {
    // 更新时序统计
    UpdateTimingStats();
    
    // 根据时序调整缓冲区
    AdjustBufferForTiming();
    
    if (!m_initialized.load() || !m_raw_sample_queue) {
        int32_t channels = m_device_channels;
        size_t bytes_per_sample = GetBytesPerSample(m_sample_format.load());
        std::memset(audioData, 0, num_frames * channels * bytes_per_sample);
        return oboe::DataCallbackResult::Continue;
    }
    
    int32_t channels = m_device_channels;
    size_t target_bytes_per_sample = GetBytesPerSample(m_sample_format.load());
    size_t bytes_requested = num_frames * channels * target_bytes_per_sample;
    
    // 从原始样本队列读取数据
    uint8_t* output = static_cast<uint8_t*>(audioData);
    size_t bytes_read = m_raw_sample_queue->ReadRaw(output, bytes_requested, m_sample_format.load(), channels);
    
    // 如果数据不足，填充静音
    if (bytes_read < bytes_requested) {
        size_t bytes_remaining = bytes_requested - bytes_read;
        std::memset(output + bytes_read, 0, bytes_remaining);
        m_underrun_count++;
    }
    
    // 应用音量控制
    ApplyVolume(audioData, num_frames, m_sample_format.load(), channels, m_volume.load());
    
    m_frames_played += num_frames;
    
    // 更新内存使用统计
    m_buffer_memory_usage.store(m_raw_sample_queue->GetMemoryUsage());
    
    return oboe::DataCallbackResult::Continue;
}

void OboeAudioRenderer::OnStreamErrorAfterClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    
    if (m_initialized.load()) {
        CloseStream();
        ConfigureAndOpenStream();
    }
}

void OboeAudioRenderer::OnStreamErrorBeforeClose(oboe::AudioStream* audioStream, oboe::Result error) {
    std::lock_guard<std::mutex> lock(m_stream_mutex);
    m_stream_started.store(false);
}

OboeAudioRenderer::PerformanceStats OboeAudioRenderer::GetStats() const {
    PerformanceStats stats;
    stats.frames_written = m_frames_written.load();
    stats.frames_played = m_frames_played.load();
    stats.underrun_count = m_underrun_count.load();
    stats.stream_restart_count = m_stream_restart_count.load();
    stats.audio_api = m_current_audio_api;
    stats.sharing_mode = m_current_sharing_mode;
    stats.sample_format = m_current_sample_format;
    stats.sample_rate = m_sample_rate.load();
    stats.frames_per_burst = m_frames_per_burst.load();
    stats.buffer_size = m_buffer_size.load();
    stats.buffer_memory_usage = m_buffer_memory_usage.load();
    stats.format_conversion_count = m_format_conversion_count.load();
    stats.adpcm_decoded_count = m_adpcm_decoded_count.load();
    stats.buffer_overrun_count = m_buffer_overrun_count.load();
    stats.timing_adjustments = m_timing_adjustments.load();
    return stats;
}

} // namespace RyujinxOboe