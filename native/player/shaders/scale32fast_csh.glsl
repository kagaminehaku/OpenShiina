// The setting "Fast zoom": the SSE2 build of scale32 (scale32sse_csh) in fewer steps, not to the
// byte. From the same tables of rows and columns it takes each destination pixel's footprint on the
// source (its partly covered first source pixel's part, its whole ones, its partly covered last's),
// reads the source at the footprint's middle mixing the 2 x 2 nearest pixels, and scales that by how
// much of the destination pixel the footprint covers (the edges of a rectangle that starts or ends
// inside a pixel, as the exact sums have them). For zooms between 1 and 2 (the copies that take the
// time) the eye cannot tell it from the exact sums; tests and gpu-check keep the exact kernel.
#version 460

layout (local_size_x = 32, local_size_y = 8) in;

layout (std430, binding = 0) readonly buffer Source { uint source[]; };
layout (std430, binding = 1) readonly buffer Rows { uint rows[]; };
layout (std430, binding = 2) readonly buffer Columns { uint columns[]; };
layout (std430, binding = 3) writeonly buffer Output { uint pixels[]; };

layout (std140, binding = 0) uniform Parameters
{
    int rowCount;
    int columnCount;
    int sourceWord;
    int sourcePitch;
    int outputWord;
    int outputPitch;
    float wholeX;
    float wholeY;
    float wholeXY;
    int maxCount;
    // The source rectangle's size in pixels (the tables' columns and rows)
    int sourceWidth;
    int sourceHeight;
};

vec4 Pixel(int line, int p)
{
    uint w = source[sourceWord + line * sourcePitch + p];
    return vec4(float(w & 0xFFu), float((w >> 8) & 0xFFu), float((w >> 16) & 0xFFu), float(w >> 24));
}

// An axis's entry (flags, whole count, first and last weights, first source pixel): the middle of
// its footprint in source pixels, and the part of the destination pixel it covers
vec2 Footprint(uint flags, uint count, float first, float last, int from, float whole)
{
    bool hasFirst = (flags & 0x80u) != 0u, hasLast = (flags & 0x40u) != 0u;
    float start = hasFirst ? float(from + 1) - first / whole : float(from);
    float wholeStart = float(from + (hasFirst ? 1 : 0));
    float end = wholeStart + float(count) + (hasLast ? last / whole : 0.0);
    float cover = (hasFirst ? first : 0.0) + float(count) * whole + (hasLast ? last : 0.0);
    return vec2((start + end) * 0.5, cover);
}

void main()
{
    int c = int(gl_GlobalInvocationID.x), r = int(gl_GlobalInvocationID.y);
    if (c >= columnCount || r >= rowCount)
        return;
    vec2 x = Footprint(columns[c * 5], columns[c * 5 + 1], uintBitsToFloat(columns[c * 5 + 2]), uintBitsToFloat(columns[c * 5 + 3]),
                       int(columns[c * 5 + 4]), wholeX);
    vec2 y = Footprint(rows[r * 5], rows[r * 5 + 1], uintBitsToFloat(rows[r * 5 + 2]), uintBitsToFloat(rows[r * 5 + 3]), int(rows[r * 5 + 4]),
                       wholeY);
    // The 2 x 2 source pixels around the middle (pixel k's middle is at k + 0.5)
    float sx = clamp(x.x - 0.5, 0.0, float(sourceWidth - 1)), sy = clamp(y.x - 0.5, 0.0, float(sourceHeight - 1));
    int x0 = int(sx), y0 = int(sy);
    int x1 = min(x0 + 1, sourceWidth - 1), y1 = min(y0 + 1, sourceHeight - 1);
    float fx = sx - float(x0), fy = sy - float(y0);
    vec4 top = mix(Pixel(y0, x0), Pixel(y0, x1), fx);
    vec4 bottom = mix(Pixel(y1, x0), Pixel(y1, x1), fx);
    vec4 v = mix(top, bottom, fy) * (x.y * y.y);
    ivec4 b = clamp(ivec4(roundEven(v)), ivec4(0), ivec4(255));
    pixels[outputWord + r * outputPitch + c] = uint(b.x) | (uint(b.y) << 8) | (uint(b.z) << 16) | (uint(b.w) << 24);
}
