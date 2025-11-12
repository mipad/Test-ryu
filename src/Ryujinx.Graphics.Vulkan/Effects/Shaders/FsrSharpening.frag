#version 450
#extension GL_ARB_separate_shader_objects : enable
#extension GL_GOOGLE_include_directive : enable

layout(location = 0) in vec2 frag_texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 1) uniform sampler2D InputTexture;

// RCAS常量
layout(binding = 2) uniform RcasConstants {
    uvec4 con;
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
    // 例如：从sRGB到线性的转换
}

void main() {
    // 将纹理坐标转换为像素坐标 - 这是关键修复
    ivec2 texture_size = textureSize(InputTexture, 0);
    AU2 ip = AU2(frag_texcoord * vec2(texture_size));
    
    AF1 pixR, pixG, pixB;
    
    // 调用RCAS锐化
    FsrRcasF(pixR, pixG, pixB, ip, constants.con);
    
    frag_color = vec4(pixR, pixG, pixB, 1.0);
}
