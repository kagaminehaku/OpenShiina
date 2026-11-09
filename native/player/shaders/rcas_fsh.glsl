// FSR 1's RCAS (robust contrast-adaptive sharpening) of what EASU drew, to the screen, as the C#
// player's Upscalers.cs (from AMD's ffx_fsr1.h; MIT, Copyright (c) 2021 Advanced Micro Devices,
// Inc.). The sharpness is in stops (exp2(-stops)): 0 stops the strongest.
#version 460

layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 outColor;

layout (binding = 0) uniform sampler2D tex;    // EASU's picture, the size it has on the screen

layout (std140, binding = 0) uniform Picture
{
    vec4 src;    // width, height (of EASU's picture), 1 / width, 1 / height
    vec4 dst;    // width, height on the screen, (unused), sharpness
} picture;

vec3 Tap(vec2 p)
{
    ivec2 size = ivec2(picture.src.xy);
    return texelFetch(tex, clamp(ivec2(floor(p)), ivec2(0), size - 1), 0).rgb;
}

void main()
{
    vec2 p = floor(vUv * picture.src.xy);
    vec3 b = Tap(p + vec2(0, -1));
    vec3 d = Tap(p + vec2(-1, 0));
    vec3 e = Tap(p);
    vec3 f = Tap(p + vec2(1, 0));
    vec3 h = Tap(p + vec2(0, 1));
    vec3 mn4 = min(min(b, d), min(f, h));
    vec3 mx4 = max(max(b, d), max(f, h));
    vec3 hitMin = min(mn4, e) / max(4.0 * mx4, vec3(1.0 / 65536.0));
    vec3 hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, vec3(-1.0 / 65536.0));
    vec3 lobeRGB = max(-hitMin, hitMax);
    float lobe = max(-(0.25 - 1.0 / 16.0), min(max(max(lobeRGB.r, lobeRGB.g), lobeRGB.b), 0.0)) * picture.dst.w;
    float rcpL = 1.0 / (4.0 * lobe + 1.0);
    outColor = vec4((lobe * (b + d + h + f) + e) * rcpL, 1.0);
}
