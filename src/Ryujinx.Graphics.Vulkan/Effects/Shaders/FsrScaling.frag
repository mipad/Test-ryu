#version 450
#extension GL_ARB_separate_shader_objects : enable
#extension GL_GOOGLE_include_directive : enable

layout(location = 0) in vec2 texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 1) uniform sampler2D InputTexture;


layout(binding = 2) uniform EasuConstants {
    vec4 con0;
    vec4 con1; 
    vec4 con2;
    vec4 con3;
} constants;

#define A_GPU 1
#define A_GLSL 1
#define FSR_EASU_F 1  
#include "ffx_a.h"
#include "ffx_fsr1.h"

AF4 FsrEasuRF(AF2 p) { return textureGather(InputTexture, p, 0); }
AF4 FsrEasuGF(AF2 p) { return textureGather(InputTexture, p, 1); }
AF4 FsrEasuBF(AF2 p) { return textureGather(InputTexture, p, 2); }

void main() {
    AU2 pos = AU2(gl_FragCoord.xy);
    AF3 color;
    
    FsrEasuF(color, pos, 
        AU1(constants.con0), 
        AU1(constants.con1), 
        AU1(constants.con2), 
        AU1(constants.con3));
        
    frag_color = vec4(color, 1.0);
}