#version 450
#extension GL_ARB_separate_shader_objects : enable
#extension GL_GOOGLE_include_directive : enable

layout(location = 0) in vec2 texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 1) uniform sampler2D InputTexture;

// RCAS常量
layout(binding = 2) uniform SharpeningParams {
    float sharpness;
    float width;
    float height;
} params;

#define A_GPU 1
#define A_GLSL 1
#include "ffx_a.h"
#include "ffx_fsr1.h"

AF4 FsrRcasLoadF(ASU2 p) { 
    ivec2 texSize = textureSize(InputTexture, 0);
    vec2 uv = vec2(p) / vec2(texSize);
    return texture(InputTexture, uv); 
}

void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) {}

void main() {
    AU2 pos = AU2(texcoord * vec2(params.width, params.height));
    AF3 color;
    
    // 计算RCAS常量
    uvec4 const0;
    FsrRcasCon(const0, params.sharpness);
    
    FsrRcasF(color.r, color.g, color.b, pos, const0);
    frag_color = vec4(color, 1.0);
}