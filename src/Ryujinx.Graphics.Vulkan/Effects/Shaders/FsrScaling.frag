#version 450
#extension GL_ARB_separate_shader_objects : enable
#extension GL_GOOGLE_include_directive : enable

layout(location = 0) in vec2 texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 1) uniform sampler2D InputTexture;

// EASU常量
layout(binding = 2) uniform EasuConstants {
    vec4 con0;
    vec4 con1; 
    vec4 con2;
    vec4 con3;
} constants;

// 必须在包含头文件之前定义这些宏
#define A_GPU 1
#define A_GLSL 1
#define FSR_EASU_F 1

// 确保类型定义正确
#define AF1 float
#define AF2 vec2
#define AF3 vec3
#define AF4 vec4
#define AU1 uint
#define AU2 uvec2
#define AU4 uvec4
#define AH1 float
#define AH2 vec2
#define AH3 vec3
#define AH4 vec4

#include "ffx_a.h"
#include "ffx_fsr1.h"

// 修复回调函数定义
AF4 FsrEasuRF(AF2 p) { 
    return textureGather(InputTexture, p, 0); 
}

AF4 FsrEasuGF(AF2 p) { 
    return textureGather(InputTexture, p, 1); 
}

AF4 FsrEasuBF(AF2 p) { 
    return textureGather(InputTexture, p, 2); 
}

void main() {
    AU2 pos = AU2(gl_FragCoord.xy);
    AF3 color;
    
    // 确保参数类型匹配
    FsrEasuF(color, pos, 
        constants.con0,  // 直接使用vec4，不转换
        constants.con1, 
        constants.con2, 
        constants.con3);
        
    frag_color = vec4(color, 1.0);
}
