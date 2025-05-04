#version 450
#extension GL_ARB_separate_shader_objects : enable

layout(binding = 0) uniform sampler2D screenTexture;
layout(binding = 1) uniform StretchParams {
    vec2 stretchFactors;
} params;

layout(location = 0) in vec2 fragTexCoord;
layout(location = 0) out vec4 outColor;

void main() {
    vec2 stretchedUV = fragTexCoord * params.stretchFactors;
    outColor = texture(screenTexture, stretchedUV);
}
