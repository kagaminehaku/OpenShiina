// FSR 1's EASU (edge-adaptive spatial upsampling): the game's picture to the size it has on the
// screen, as the C# player's Upscalers.cs (ported there from AMD's ffx_fsr1.h, its 32-bit float
// path; MIT, Copyright (c) 2021 Advanced Micro Devices, Inc.). 12 taps, a Lanczos-like window
// stretched along the edge, deringed to the 4 nearest. Drawn into a picture of the screen size,
// which RCAS (rcas_fsh) sharpens to the screen.
#version 460

layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 outColor;

layout (binding = 0) uniform sampler2D tex;    // the game's picture

layout (std140, binding = 0) uniform Picture
{
    vec4 src;    // width, height, 1 / width, 1 / height
    vec4 dst;    // width, height of the output
} picture;

vec3 Tap(vec2 p)
{
    ivec2 size = ivec2(picture.src.xy);
    return texelFetch(tex, clamp(ivec2(floor(p)), ivec2(0), size - 1), 0).rgb;
}

float Luma(vec3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }
float Rcp(float x) { return 1.0 / max(x, 1.0 / 65536.0); }

void SetDir(inout vec2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE)
{
    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = Rcp(max(abs(dc), abs(cb)));
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = clamp(abs(dirX) * lenX, 0.0, 1.0);
    len += lenX * lenX * w;
    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = Rcp(max(abs(ec), abs(ca)));
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = clamp(abs(dirY) * lenY, 0.0, 1.0);
    len += lenY * lenY * w;
}

void AddTap(inout vec3 aC, inout float aW, vec2 off, vec2 dir, vec2 len, float lob, float clp, vec3 c)
{
    vec2 v = vec2(off.x * dir.x + off.y * dir.y, off.x * -dir.y + off.y * dir.x) * len;
    float d2 = min(v.x * v.x + v.y * v.y, clp);
    float wB = 0.4 * d2 - 1.0;
    float wA = lob * d2 - 1.0;
    wB *= wB;
    wA *= wA;
    wB = 25.0 / 16.0 * wB - (25.0 / 16.0 - 1.0);
    float w = wB * wA;
    aC += c * w;
    aW += w;
}

void main()
{
    vec2 scale = picture.src.xy / picture.dst.xy;
    vec2 offset = 0.5 * scale - 0.5;
    // the output pixel (its top left corner, as the C# shader's floor(p))
    vec2 pp = floor(vUv * picture.dst.xy) * scale + offset;
    vec2 fp = floor(pp);
    pp -= fp;
    vec3 b = Tap(fp + vec2(0, -1));
    vec3 c = Tap(fp + vec2(1, -1));
    vec3 e = Tap(fp + vec2(-1, 0));
    vec3 f = Tap(fp);
    vec3 g = Tap(fp + vec2(1, 0));
    vec3 h = Tap(fp + vec2(2, 0));
    vec3 i = Tap(fp + vec2(-1, 1));
    vec3 j = Tap(fp + vec2(0, 1));
    vec3 k = Tap(fp + vec2(1, 1));
    vec3 l = Tap(fp + vec2(2, 1));
    vec3 n = Tap(fp + vec2(0, 2));
    vec3 o = Tap(fp + vec2(1, 2));
    float bL = Luma(b), cL = Luma(c), eL = Luma(e), fL = Luma(f), gL = Luma(g), hL = Luma(h);
    float iL = Luma(i), jL = Luma(j), kL = Luma(k), lL = Luma(l), nL = Luma(n), oL = Luma(o);
    vec2 dir = vec2(0);
    float len = 0.0;
    SetDir(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
    SetDir(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
    SetDir(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
    SetDir(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);
    vec2 dir2 = dir * dir;
    float dirR = dir2.x + dir2.y;
    bool zro = dirR < 1.0 / 32768.0;
    dirR = zro ? 1.0 : inversesqrt(dirR);
    dir.x = zro ? 1.0 : dir.x;
    dir *= dirR;
    len = len * 0.5;
    len *= len;
    float stretch = (dir.x * dir.x + dir.y * dir.y) * Rcp(max(abs(dir.x), abs(dir.y)));
    vec2 len2 = vec2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
    float clp = Rcp(lob);
    vec3 min4 = min(min(f, g), min(j, k));
    vec3 max4 = max(max(f, g), max(j, k));
    vec3 aC = vec3(0);
    float aW = 0.0;
    AddTap(aC, aW, vec2(0, -1) - pp, dir, len2, lob, clp, b);
    AddTap(aC, aW, vec2(1, -1) - pp, dir, len2, lob, clp, c);
    AddTap(aC, aW, vec2(-1, 1) - pp, dir, len2, lob, clp, i);
    AddTap(aC, aW, vec2(0, 1) - pp, dir, len2, lob, clp, j);
    AddTap(aC, aW, vec2(0, 0) - pp, dir, len2, lob, clp, f);
    AddTap(aC, aW, vec2(-1, 0) - pp, dir, len2, lob, clp, e);
    AddTap(aC, aW, vec2(1, 1) - pp, dir, len2, lob, clp, k);
    AddTap(aC, aW, vec2(2, 1) - pp, dir, len2, lob, clp, l);
    AddTap(aC, aW, vec2(2, 0) - pp, dir, len2, lob, clp, h);
    AddTap(aC, aW, vec2(1, 0) - pp, dir, len2, lob, clp, g);
    AddTap(aC, aW, vec2(1, 2) - pp, dir, len2, lob, clp, o);
    AddTap(aC, aW, vec2(0, 2) - pp, dir, len2, lob, clp, n);
    outColor = vec4(min(max4, max(min4, aC / aW)), 1.0);
}
