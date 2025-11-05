#version 450 core
layout (local_size_x = 16, local_size_y = 16) in;
layout(rgba8, binding = 0) uniform image2D imgOutput;
layout(binding = 1) uniform sampler2D Source;

// 统一变量 - 源和目标尺寸
layout(binding = 2) uniform Dimensions {
    float srcX0;
    float srcX1;
    float srcY0;
    float srcY1;
    float dstX0;
    float dstX1;
    float dstY0;
    float dstY1;
};

// 安全采样函数
vec4 safeTexture(vec2 texCoord) {
    texCoord = clamp(texCoord, vec2(0.0), vec2(1.0));
    return texture(Source, texCoord);
}

// MMPX 算法函数
float luma(vec4 col) {
    return dot(col.rgb, vec3(0.2126, 0.7152, 0.0722)) * (1.0 - col.a);
}

bool same(vec4 B, vec4 A0) {
    return all(equal(B, A0));
}

bool notsame(vec4 B, vec4 A0) {
    return any(notEqual(B, A0));
}

bool all_eq2(vec4 B, vec4 A0, vec4 A1) {
    return (same(B,A0) && same(B,A1));
}

bool all_eq3(vec4 B, vec4 A0, vec4 A1, vec4 A2) {
    return (same(B,A0) && same(B,A1) && same(B,A2));
}

bool all_eq4(vec4 B, vec4 A0, vec4 A1, vec4 A2, vec4 A3) {
    return (same(B,A0) && same(B,A1) && same(B,A2) && same(B,A3));
}

bool any_eq3(vec4 B, vec4 A0, vec4 A1, vec4 A2) {
    return (same(B,A0) || same(B,A1) || same(B,A2));
}

bool none_eq2(vec4 B, vec4 A0, vec4 A1) {
    return (notsame(B,A0) && notsame(B,A1));
}

bool none_eq4(vec4 B, vec4 A0, vec4 A1, vec4 A2, vec4 A3) {
    return (notsame(B,A0) && notsame(B,A1) && notsame(B,A2) && notsame(B,A3));
}

// 边界检查
float insideBox(vec2 v, vec2 bottomLeft, vec2 topRight) {
    vec2 s = step(bottomLeft, v) - step(topRight, v);
    return s.x * s.y;
}

void main() {
    ivec2 dstCoord = ivec2(gl_GlobalInvocationID.xy);
    
    // 边界检查
    vec2 bottomLeft = vec2(min(dstX0, dstX1), min(dstY0, dstY1));
    vec2 topRight = vec2(max(dstX0, dstX1), max(dstY0, dstY1));
    
    if (insideBox(vec2(dstCoord), bottomLeft, topRight) == 0.0) {
        return;
    }
    
    // 计算源和目标尺寸
    vec2 source_size = vec2(abs(srcX1 - srcX0), abs(srcY1 - srcY0));
    vec2 target_size = vec2(abs(dstX1 - dstX0), abs(dstY1 - dstY0));
    
    // 计算纹理坐标
    vec2 tex_coord = (vec2(dstCoord) + vec2(0.5)) / target_size;
    vec2 pos = fract(tex_coord * source_size) - vec2(0.5, 0.5);
    vec2 coord = tex_coord - pos / source_size;
    
    // 定义采样宏
    #define src(x, y) safeTexture(coord + vec2(x, y) / source_size)
    
    // 采样周围像素
    vec4 E = src(0.0,0.0);

    vec4 A = src(-1.0,-1.0);
    vec4 B = src(0.0,-1.0);
    vec4 C = src(1.0,-1.0);

    vec4 D = src(-1.0,0.0);
    vec4 F = src(1.0,0.0);

    vec4 G = src(-1.0,1.0);
    vec4 H = src(0.0,1.0);
    vec4 I = src(1.0,1.0);

    vec4 J = E;
    vec4 K = E;
    vec4 L = E;
    vec4 M = E;

    vec4 color = E;

    // 如果周围都是相同颜色，直接返回
    if(same(E,A) && same(E,B) && same(E,C) && same(E,D) && same(E,F) && same(E,G) && same(E,H) && same(E,I)) {
        imageStore(imgOutput, dstCoord, color);
        return;
    }

    // 扩展采样区域
    vec4 P  = src(0.0,2.0);
    vec4 Q  = src(-2.0,0.0);
    vec4 R  = src(2.0,0.0);
    vec4 S  = src(0.0,2.0);

    // 计算亮度
    float Bl = luma(B);
    float Dl = luma(D);
    float El = luma(E);
    float Fl = luma(F);
    float Hl = luma(H);

    // 修复条件判断 - 简化复杂的括号结构
    bool cond1 = same(D,B) && notsame(D,H) && notsame(D,F);
    bool cond2 = El>=Dl || same(E,A);
    bool cond3 = any_eq3(E,A,C,G);
    bool cond4 = El<Dl || notsame(A,D) || notsame(E,P) || notsame(E,Q);
    if (cond1 && cond2 && cond3 && cond4) J = mix(D, J, 0.5);
    
    cond1 = same(B,F) && notsame(B,D) && notsame(B,H);
    cond2 = El>=Bl || same(E,C);
    cond3 = any_eq3(E,A,C,I);
    cond4 = El<Bl || notsame(C,B) || notsame(E,P) || notsame(E,R);
    if (cond1 && cond2 && cond3 && cond4) K = mix(B, K, 0.5);
    
    cond1 = same(H,D) && notsame(H,F) && notsame(H,B);
    cond2 = El>=Hl || same(E,G);
    cond3 = any_eq3(E,A,G,I);
    cond4 = El<Hl || notsame(G,H) || notsame(E,S) || notsame(E,Q);
    if (cond1 && cond2 && cond3 && cond4) L = mix(H, L, 0.5);
    
    cond1 = same(F,H) && notsame(F,B) && notsame(F,D);
    cond2 = El>=Fl || same(E,I);
    cond3 = any_eq3(E,C,G,I);
    cond4 = El<Fl || notsame(I,H) || notsame(E,R) || notsame(E,S);
    if (cond1 && cond2 && cond3 && cond4) M = mix(F, M, 0.5);

    // 其他条件判断也进行简化
    if (notsame(E,F) && all_eq4(E,C,I,D,Q) && all_eq2(F,B,H) && notsame(F,src(3.0,0.0))) {
        M = mix(M, F, 0.5);
        K = mix(K, M, 0.5);
    }
    
    if (notsame(E,D) && all_eq4(E,A,G,F,R) && all_eq2(D,B,H) && notsame(D,src(-3.0,0.0))) {
        L = mix(L, D, 0.5);
        J = mix(J, L, 0.5);
    }
    
    if (notsame(E,H) && all_eq4(E,G,I,B,P) && all_eq2(H,D,F) && notsame(H,src(0.0,3.0))) {
        M = mix(M, H, 0.5);
        L = mix(L, M, 0.5);
    }
    
    if (notsame(E,B) && all_eq4(E,A,C,H,S) && all_eq2(B,D,F) && notsame(B,src(0.0,-3.0))) {
        K = mix(K, B, 0.5);
        J = mix(J, K, 0.5);
    }

    if (Bl<El && all_eq4(E,G,H,I,S) && none_eq4(E,A,D,C,F)) {
        K = mix(K, B, 0.5);
        J = mix(J, K, 0.5);
    }
    
    if (Hl<El && all_eq4(E,A,B,C,P) && none_eq4(E,D,G,I,F)) {
        M = mix(M, H, 0.5);
        L = mix(L, M, 0.5);
    }
    
    if (Fl<El && all_eq4(E,A,D,G,Q) && none_eq4(E,B,C,I,H)) {
        M = mix(M, F, 0.5);
        K = mix(K, M, 0.5);
    }
    
    if (Dl<El && all_eq4(E,C,F,I,R) && none_eq4(E,B,A,G,H)) {
        L = mix(L, D, 0.5);
        J = mix(J, L, 0.5);
    }

    if (notsame(H,B)) {
        if (notsame(H,A) && notsame(H,E) && notsame(H,C)) {
            if (all_eq3(H,G,F,R) && none_eq2(H,D,src(2.0,-1.0))) L = mix(M, L, 0.5);
            if (all_eq3(H,I,D,Q) && none_eq2(H,F,src(-2.0,-1.0))) M = mix(L, M, 0.5);
        }

        if (notsame(B,I) && notsame(B,G) && notsame(B,E)) {
            if (all_eq3(B,A,F,R) && none_eq2(B,D,src(2.0,1.0))) J = mix(K, L, 0.5);
            if (all_eq3(B,C,D,Q) && none_eq2(B,F,src(-2.0,1.0))) K = mix(J, K, 0.5);
        }
    }

    if (notsame(F,D)) {
        if (notsame(D,I) && notsame(D,E) && notsame(D,C)) {
            if (all_eq3(D,A,H,S) && none_eq2(D,B,src(1.0,2.0))) J = mix(L, J, 0.5);
            if (all_eq3(D,G,B,P) && none_eq2(D,H,src(1.0,2.0))) L = mix(J, L, 0.5);
        }

        if (notsame(F,E) && notsame(F,A) && notsame(F,G)) {
            if (all_eq3(F,C,H,S) && none_eq2(F,B,src(-1.0,2.0))) K = mix(M, K, 0.5);
            if (all_eq3(F,I,B,P) && none_eq2(F,H,src(-1.0,-2.0))) M = mix(K, M, 0.5);
        }
    }

    // 最终颜色选择
    vec2 a = fract(tex_coord * source_size);
    color = (a.x < 0.5) ? (a.y < 0.5 ? J : L) : (a.y < 0.5 ? K : M);
    
    imageStore(imgOutput, dstCoord, color);
}