#version 450
#extension GL_ARB_separate_shader_objects : enable

layout(location = 0) in vec2 texcoord;
layout(location = 0) out vec4 frag_color;

layout(binding = 1) uniform sampler2D InputTexture;

layout(binding = 2) uniform RcasConstants {
    vec4 con;
} constants;

// 简化的RCAS锐化实现
void main() {
    ivec2 ip = ivec2(gl_FragCoord.xy);
    
    // 获取3x3邻域
    vec3 c = texelFetch(InputTexture, ip + ivec2( 0, 0), 0).rgb;
    vec3 n = texelFetch(InputTexture, ip + ivec2( 0,-1), 0).rgb;
    vec3 e = texelFetch(InputTexture, ip + ivec2( 1, 0), 0).rgb;
    vec3 s = texelFetch(InputTexture, ip + ivec2( 0, 1), 0).rgb;
    vec3 w = texelFetch(InputTexture, ip + ivec2(-1, 0), 0).rgb;
    
    float sharpness = constants.con.x; // 锐化强度
    
    // 计算亮度
    float cL = dot(c, vec3(0.299, 0.587, 0.114));
    float nL = dot(n, vec3(0.299, 0.587, 0.114));
    float eL = dot(e, vec3(0.299, 0.587, 0.114));
    float sL = dot(s, vec3(0.299, 0.587, 0.114));
    float wL = dot(w, vec3(0.299, 0.587, 0.114));
    
    // 计算高频细节（拉普拉斯算子）
    float detail = (nL + eL + sL + wL) * 0.25 - cL;
    
    // 自适应锐化 - 基于局部对比度调整强度
    float localContrast = max(max(max(nL, eL), max(sL, wL)) - min(min(min(nL, eL), min(sL, wL)), 0.1);
    float adaptiveSharpness = sharpness * clamp(localContrast * 4.0, 0.0, 1.0);
    
    // 应用锐化
    vec3 result = c + detail * adaptiveSharpness;
    
    // 限制结果范围
    frag_color = vec4(clamp(result, 0.0, 1.0), 1.0);
}
