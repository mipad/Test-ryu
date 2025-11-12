#version 450
#extension GL_ARB_separate_shader_objects : enable
#extension GL_GOOGLE_include_directive : enable

layout(location = 0) in vec2 texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 0) uniform sampler2D InputTexture;

// EASU常量 - 使用push constants
layout(push_constant) uniform EasuConstants {
    vec4 con0;
    vec4 con1; 
    vec4 con2;
    vec4 con3;
} constants;

// 必须在包含头文件之前定义这些宏
#define A_GPU 1
#define A_GLSL 1
#define FSR_EASU_F 1

// 包含必要的头文件
#include "ffx_a.h"
#include "ffx_fsr1.h"

// 修复回调函数定义 - 使用正确的GLSL类型
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
    
    // 直接使用vec4常量，ffx_a.h会自动处理类型转换
    FsrEasuF(color, pos, 
        AU4(constants.con0), 
        AU4(constants.con1), 
        AU4(constants.con2), 
        AU4(constants.con3));
        
    frag_color = vec4(color, 1.0);
}