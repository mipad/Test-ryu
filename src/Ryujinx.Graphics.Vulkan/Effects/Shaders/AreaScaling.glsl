#version 460 

layout (local_size_x = 16, local_size_y = 16) in;
layout( rgba8, binding = 0, set = 3) uniform image2D imgOutput;
layout( binding = 1, set = 2) uniform sampler2D Source;
layout( binding = 2 ) uniform dimensions{
 float srcX0;
 float srcX1;
 float srcY0;
 float srcY1;
 float dstX0;
 float dstX1;
 float dstY0;
 float dstY1;
};

/***** Area Sampling *****/

// By Sam Belliveau and Filippo Tarpini. Public Domain license.
// Effectively a more accurate sharp bilinear filter when upscaling,
// that also works as a mathematically perfect downscale filter.
// https://entropymine.com/imageworsener/pixelmixing/
// https://github.com/obsproject/obs-studio/pull/1715
// https://legacy.imagemagick.org/Usage/filter/
vec4 AreaSampling(vec2 xy)
{
    
    vec2 source_size = vec2(abs(srcX1 - srcX0), abs(srcY1 - srcY0));
    vec2 target_size = vec2(abs(dstX1 - dstX0), abs(dstY1 - dstY0));
    vec2 inverted_target_size = vec2(1.0) / target_size;

    
    vec2 t_beg = floor(xy - vec2(dstX0 < dstX1 ? dstX0 : dstX1, dstY0 < dstY1 ? dstY0 : dstY1));
    vec2 t_end = t_beg + vec2(1.0, 1.0);

    
    vec2 beg = t_beg * inverted_target_size * source_size;
    vec2 end = t_end * inverted_target_size * source_size;

    
    ivec2 f_beg = ivec2(beg);
    ivec2 f_end = ivec2(end);

    
    float area_w = 1.0 - fract(beg.x);
    float area_n = 1.0 - fract(beg.y);
    float area_e = fract(end.x);
    float area_s = fract(end.y);

    
    float area_nw = area_n * area_w;
    float area_ne = area_n * area_e;
    float area_sw = area_s * area_w;
    float area_se = area_s * area_e;

    
    vec4 avg_color = vec4(0.0, 0.0, 0.0, 0.0);

    
    avg_color += area_nw * texelFetch(Source, ivec2(f_beg.x, f_beg.y), 0);
    avg_color += area_ne * texelFetch(Source, ivec2(f_end.x, f_beg.y), 0);
    avg_color += area_sw * texelFetch(Source, ivec2(f_beg.x, f_end.y), 0);
    avg_color += area_se * texelFetch(Source, ivec2(f_end.x, f_end.y), 0);

    int x_range = int(f_end.x - f_beg.x - 0.5);
    int y_range = int(f_end.y - f_beg.y - 0.5);

    
    for (int x = f_beg.x + 1; x <= f_beg.x + x_range; ++x)
    {
        avg_color += area_n * texelFetch(Source, ivec2(x, f_beg.y), 0);
        avg_color += area_s * texelFetch(Source, ivec2(x, f_end.y), 0);
    }

   
    for (int y = f_beg.y + 1; y <= f_beg.y + y_range; ++y)
    {
        avg_color += area_w * texelFetch(Source, ivec2(f_beg.x, y), 0);
        avg_color += area_e * texelFetch(Source, ivec2(f_end.x, y), 0);

        for (int x = f_beg.x + 1; x <= f_beg.x + x_range; ++x)
        {
            avg_color += texelFetch(Source, ivec2(x, y), 0);
        }
    }

    
    float area_corners = area_nw + area_ne + area_sw + area_se;
    float area_edges = float(x_range) * (area_n + area_s) + float(y_range) * (area_w + area_e);
    float area_center = float(x_range) * float(y_range);

    
    return avg_color / (area_corners + area_edges + area_center);
}

float insideBox(vec2 v, vec2 bottomLeft, vec2 topRight) {
    vec2 s = step(bottomLeft, v) - step(topRight, v);
    return s.x * s.y;
}


vec2 convertCoords(vec2 pos) {
    
    if (dstY1 < dstY0) {
        pos.y = dstY0 - pos.y + dstY1;
    }
    return pos;
}

void main()
{
    ivec2 loc = ivec2(gl_GlobalInvocationID.x, gl_GlobalInvocationID.y);
    
    vec2 bottomLeft = vec2(min(dstX0, dstX1), min(dstY0, dstY1));
    vec2 topRight = vec2(max(dstX0, dstX1), max(dstY0, dstY1));
    
    if (insideBox(vec2(loc), bottomLeft, topRight) == 0.0) {

        imageStore(imgOutput, loc, vec4(0, 0, 0, 1));
        return;
    }

    
    vec2 samplePos = convertCoords(vec2(loc));
    
    
    vec4 outColor = AreaSampling(samplePos);
    
    
    imageStore(imgOutput, loc, vec4(outColor.rgb, 1.0));
}
