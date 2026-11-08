// The game's picture to the screen, as the C# player's setting "Scaling" says (Game/PictureScaling.cs):
//   0 nearest-neighbour (also the whole-number scales, where it is exact)
//   1 smooth: bicubic (Catmull-Rom, 4 x 4 pixels)
//   2 sharp: the picture multiplied by a whole number with nearest-neighbour, the rest of the way
//     bilinear ("sharp bilinear": each game pixel a flat square, only its edges blended)
#version 460

layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 outColor;

layout (binding = 0) uniform sampler2D tex;    // linear filtering

layout (std140, binding = 0) uniform Picture
{
    vec4 src;    // width, height, 1 / width, 1 / height
    vec4 dst;    // width, height on the screen, mode
} picture;

vec3 Pixel(ivec2 p)
{
    ivec2 size = ivec2(picture.src.xy);
    return texelFetch(tex, clamp(p, ivec2(0), size - 1), 0).rgb;
}

vec4 CatmullRom(float t)
{
    float t2 = t * t, t3 = t2 * t;
    return vec4(-0.5 * t3 + t2 - 0.5 * t, 1.5 * t3 - 2.5 * t2 + 1.0, -1.5 * t3 + 2.0 * t2 + 0.5 * t, 0.5 * t3 - 0.5 * t2);
}

void main()
{
    vec2 texel = vUv * picture.src.xy;
    float mode = picture.dst.z;
    if (mode < 0.5)
    {
        outColor = vec4(Pixel(ivec2(floor(texel))), 1.0);
    }
    else if (mode < 1.5)
    {
        vec2 p = texel - 0.5;
        ivec2 base = ivec2(floor(p));
        vec2 f = p - floor(p);
        vec4 wx = CatmullRom(f.x), wy = CatmullRom(f.y);
        vec3 sum = vec3(0.0);
        for (int y = 0; y < 4; y++)
        {
            vec3 row = vec3(0.0);
            for (int x = 0; x < 4; x++)
                row += Pixel(base + ivec2(x - 1, y - 1)) * wx[x];
            sum += row * wy[y];
        }
        outColor = vec4(clamp(sum, 0.0, 1.0), 1.0);
    }
    else
    {
        vec2 scale = max(floor(picture.dst.xy * picture.src.zw), vec2(1.0));
        vec2 inside = fract(texel) - 0.5;
        vec2 region = 0.5 - 0.5 / scale;
        vec2 f = (inside - clamp(inside, -region, region)) * scale + 0.5;
        outColor = vec4(texture(tex, (floor(texel) + f) * picture.src.zw).rgb, 1.0);
    }
}
