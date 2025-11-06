#version 450 core
layout(location = 0) in vec2 tex_coord;
layout(location = 0) out vec4 frag_color;

layout(binding = 0) uniform sampler2D Source;

layout(binding = 1) uniform tex_coord_in
{
    vec4 tex_coord_in_data;
};

void main()
{
    // 简单的测试：直接采样纹理并输出
    vec2 actual_tex_coord = vec2(
        tex_coord_in_data[0] + tex_coord.x * (tex_coord_in_data[1] - tex_coord_in_data[0]),
        tex_coord_in_data[2] + tex_coord.y * (tex_coord_in_data[3] - tex_coord_in_data[2])
    );
    
    frag_color = texture(Source, actual_tex_coord);
    
    // 添加一些调试输出（比如在边缘显示红色）
    if (tex_coord.x < 0.01 || tex_coord.x > 0.99 || tex_coord.y < 0.01 || tex_coord.y > 0.99)
    {
        frag_color = vec4(1.0, 0.0, 0.0, 1.0);
    }
}