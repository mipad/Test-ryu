#version 450
#extension GL_ARB_separate_shader_objects : enable
#extension GL_GOOGLE_include_directive : enable

layout(location = 0) in vec2 frag_texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 0) uniform sampler2D InputTexture;

// RCAS常量 - 使用uniform buffer
layout(binding = 2) uniform RcasConstants {
    vec4 con0;
    vec3 con1;
    float padding;
} constants;

// 核心宏定义 - 使用32位版本
#define A_GPU 1
#define A_GLSL 1
#define FSR_RCAS_F 1

#include "ffx_a.h"
#include "ffx_fsr1.h"

// 实现RCAS所需的回调函数 - 使用texelFetch
AF4 FsrRcasLoadF(ASU2 p) { 
    return texelFetch(InputTexture, p, 0); 
}

void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {
    // 可选的颜色转换，如果不需要可以留空
}

void main() {
    // 将纹理坐标转换为像素坐标
    ivec2 texture_size = textureSize(InputTexture, 0);
    AU2 ip = AU2(frag_texcoord * vec2(texture_size));
    
    AF1 pixR, pixG, pixB;
    
    // 准备RCAS常量
    AU4 rcasCon;
    rcasCon[0] = floatBitsToUint(constants.con0.x);
    rcasCon[1] = floatBitsToUint(constants.con0.y);
    rcasCon[2] = floatBitsToUint(constants.con0.z);
    rcasCon[3] = floatBitsToUint(constants.con0.w);
    
    // 调用RCAS锐化
    FsrRcasF(pixR, pixG, pixB, ip, rcasCon);
    
    frag_color = vec4(pixR, pixG, pixB, 1.0);
}