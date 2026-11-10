// The SSE2 build of scale32 (src/scripting/ScnVm.Scale32Sse.cpp) on the GPU mode's compute queue,
// one invocation a destination pixel: the CPU makes the tables of rows and columns (their flags,
// counts, partial weights and first source row or column), the shader adds up the source pixels
// times their weights in the order of the x86 code, every product rounded before its sum (precise:
// no fused multiply-adds), then to the nearest even and into 0-255 (cvtps2dq, packssdw, packuswb).
// Source and destination are words from sourceWord and outputWord, rows sourcePitch and
// outputPitch words apart: the scripts' memory itself, or copies of it.
#version 460

layout (local_size_x = 16, local_size_y = 16) in;

layout (std430, binding = 0) readonly buffer Source { uint source[]; };
// Per destination row: flags, count of whole source rows, first and last weights (float bits),
// its first source row; per destination column the same with its first source pixel
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
};

vec4 Pixel(int line, int p)
{
    uint w = source[sourceWord + line * sourcePitch + p];
    return vec4(float(w & 0xFFu), float((w >> 8) & 0xFFu), float((w >> 16) & 0xFFu), float(w >> 24));
}

// One source row's part of the pixel: its first, whole and last columns (AddRow)
vec4 AddRow(vec4 sum, int line, int p, int flags, uint count, float first, float last, float wy, float whole)
{
    precise vec4 acc = sum;
    if ((flags & 0x80) != 0)
    {
        precise float w = first * wy;
        acc += Pixel(line, p++) * w;
    }
    for (uint k = 0u; k < count; k++)
        acc += Pixel(line, p++) * whole;
    if ((flags & 0x40) != 0)
    {
        precise float w = last * wy;
        acc += Pixel(line, p) * w;
    }
    return acc;
}

void main()
{
    int c = int(gl_GlobalInvocationID.x), r = int(gl_GlobalInvocationID.y);
    if (c >= columnCount || r >= rowCount)
        return;
    int rowFlags = int(rows[r * 5]);
    uint rowCountWhole = rows[r * 5 + 1];
    float rowFirst = uintBitsToFloat(rows[r * 5 + 2]), rowLast = uintBitsToFloat(rows[r * 5 + 3]);
    int y = int(rows[r * 5 + 4]);
    int flags = int(columns[c * 5]);
    uint count = columns[c * 5 + 1];
    float first = uintBitsToFloat(columns[c * 5 + 2]), last = uintBitsToFloat(columns[c * 5 + 3]);
    int x = int(columns[c * 5 + 4]);
    precise vec4 acc = vec4(0.0);

    if (rowFlags == 0xC0 && rowCountWhole <= 1u)
    {
        int p = x;
        if (rowCountWhole == 0u)
        {
            // Rows 0xC0 with no whole row: across the two rows, column by column
            float w0 = rowFirst, w1 = rowLast;
            precise float whole0 = wholeX * w0, whole1 = wholeX * w1;
            if ((flags & 0x80) != 0)
            {
                precise float a = w0 * first, b = first * w1;
                acc += Pixel(y, p) * a;
                acc += Pixel(y + 1, p) * b;
                p++;
            }
            for (uint k = 0u; k < count; k++, p++)
            {
                acc += Pixel(y, p) * whole0;
                acc += Pixel(y + 1, p) * whole1;
            }
            if ((flags & 0x40) != 0)
            {
                precise float a = w0 * last, b = last * w1;
                acc += Pixel(y, p) * a;
                acc += Pixel(y + 1, p) * b;
            }
        }
        else
        {
            // Rows 0xC0 with one whole row: across the three rows (the partial last column with the
            // first two rows' weights swapped, as the x86 code has them)
            float w0 = rowFirst, w2 = rowLast;
            precise float whole0 = wholeX * w0, whole2 = wholeX * w2;
            if ((flags & 0x80) != 0)
            {
                precise float a = w0 * first, b = wholeY * first, d = first * w2;
                acc += Pixel(y, p) * a;
                acc += Pixel(y + 1, p) * b;
                acc += Pixel(y + 2, p) * d;
                p++;
            }
            for (uint k = 0u; k < count; k++, p++)
            {
                acc += Pixel(y, p) * whole0;
                acc += Pixel(y + 1, p) * wholeXY;
                acc += Pixel(y + 2, p) * whole2;
            }
            if ((flags & 0x40) != 0)
            {
                precise float a = wholeY * last, b = w0 * last, d = last * w2;
                acc += Pixel(y, p) * a;
                acc += Pixel(y + 1, p) * b;
                acc += Pixel(y + 2, p) * d;
            }
        }
    }
    else
    {
        // The source rows one after the other, in each the pixel's columns
        int line = y;
        if ((rowFlags & 0x80) != 0)
        {
            precise float whole = wholeX * rowFirst;
            acc = AddRow(acc, line++, x, flags, count, first, last, rowFirst, whole);
        }
        for (uint k = 0u; k < rowCountWhole; k++)
            acc = AddRow(acc, line++, x, flags, count, first, last, wholeY, wholeXY);
        if ((rowFlags & 0x40) != 0)
        {
            precise float whole = wholeX * rowLast;
            acc = AddRow(acc, line, x, flags, count, first, last, rowLast, whole);
        }
    }
    ivec4 v = clamp(ivec4(roundEven(acc)), ivec4(0), ivec4(255));
    pixels[outputWord + r * outputPitch + c] = uint(v.x) | (uint(v.y) << 8) | (uint(v.z) << 16) | (uint(v.w) << 24);
}
