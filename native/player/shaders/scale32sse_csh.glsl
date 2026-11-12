// The SSE2 build of scale32 (src/scripting/ScnVm.Scale32Sse.cpp) on the GPU mode's compute queue,
// one invocation a destination pixel: the CPU makes the tables of rows and columns (their flags,
// counts, partial weights and first source row or column), the shader adds up the source pixels
// times their weights in the order of the x86 code, every product rounded before its sum (precise:
// no fused multiply-adds), then to the nearest even and into 0-255 (cvtps2dq, packssdw, packuswb).
// Source and destination are words from sourceWord and outputWord, rows sourcePitch and
// outputPitch words apart: the scripts' memory itself, or copies of it.
//
// A warp is one destination row (32 columns), so what depends on the row is the same in all its
// invocations; what depends on the column (a partial first or last pixel, how many whole ones) is
// made the same too: every column adds a first pixel, maxCount whole ones and a last one, those it
// does not have with a weight of 0. The sums are of products that are never negative, to which
// adding 0 changes nothing: the bytes are the x86 code's, and no invocation of a warp waits while
// the others take a branch it does not.
#version 460

layout (local_size_x = 32, local_size_y = 8) in;

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
    // The largest count of whole source pixels of a column
    int maxCount;
};

vec4 Pixel(int line, int p)
{
    uint w = source[sourceWord + line * sourcePitch + p];
    return vec4(float(w & 0xFFu), float((w >> 8) & 0xFFu), float((w >> 16) & 0xFFu), float(w >> 24));
}

// The column's pixels: its partial first (or its first whole one, weighted 0), whole ones from
// pWhole (count of them, then the last pixel again, weighted 0) and its partial last (or its last
// pixel, weighted 0)
struct Column
{
    bool first, last;
    int count, pFirst, pWhole, pLast;
};

int WholeAt(Column col, int k) { return min(col.pWhole + k, col.pLast); }

// One source row's part of the pixel: its first, whole and last columns (AddRow)
vec4 AddRow(vec4 sum, int line, Column col, float first, float last, float wy, float whole)
{
    precise vec4 acc = sum;
    precise float wf = col.first ? first * wy : 0.0;
    acc += Pixel(line, col.pFirst) * wf;
    for (int k = 0; k < maxCount; k++)
    {
        float w = k < col.count ? whole : 0.0;
        acc += Pixel(line, WholeAt(col, k)) * w;
    }
    precise float wl = col.last ? last * wy : 0.0;
    acc += Pixel(line, col.pLast) * wl;
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
    float first = uintBitsToFloat(columns[c * 5 + 2]), last = uintBitsToFloat(columns[c * 5 + 3]);
    Column col;
    col.first = (flags & 0x80) != 0;
    col.last = (flags & 0x40) != 0;
    col.count = int(columns[c * 5 + 1]);
    col.pFirst = int(columns[c * 5 + 4]);
    col.pWhole = col.pFirst + (col.first ? 1 : 0);
    col.pLast = max(col.pWhole + col.count + (col.last ? 0 : -1), col.pFirst);
    precise vec4 acc = vec4(0.0);

    if (rowFlags == 0xC0 && rowCountWhole <= 1u)
    {
        if (rowCountWhole == 0u)
        {
            // Rows 0xC0 with no whole row: across the two rows, column by column
            float w0 = rowFirst, w1 = rowLast;
            precise float whole0 = wholeX * w0, whole1 = wholeX * w1;
            precise float a = col.first ? w0 * first : 0.0, b = col.first ? first * w1 : 0.0;
            acc += Pixel(y, col.pFirst) * a;
            acc += Pixel(y + 1, col.pFirst) * b;
            for (int k = 0; k < maxCount; k++)
            {
                int p = WholeAt(col, k);
                float m0 = k < col.count ? whole0 : 0.0, m1 = k < col.count ? whole1 : 0.0;
                acc += Pixel(y, p) * m0;
                acc += Pixel(y + 1, p) * m1;
            }
            precise float al = col.last ? w0 * last : 0.0, bl = col.last ? last * w1 : 0.0;
            acc += Pixel(y, col.pLast) * al;
            acc += Pixel(y + 1, col.pLast) * bl;
        }
        else
        {
            // Rows 0xC0 with one whole row: across the three rows (the partial last column with the
            // first two rows' weights swapped, as the x86 code has them)
            float w0 = rowFirst, w2 = rowLast;
            precise float whole0 = wholeX * w0, whole2 = wholeX * w2;
            precise float a = col.first ? w0 * first : 0.0, b = col.first ? wholeY * first : 0.0, d = col.first ? first * w2 : 0.0;
            acc += Pixel(y, col.pFirst) * a;
            acc += Pixel(y + 1, col.pFirst) * b;
            acc += Pixel(y + 2, col.pFirst) * d;
            for (int k = 0; k < maxCount; k++)
            {
                int p = WholeAt(col, k);
                bool on = k < col.count;
                float m0 = on ? whole0 : 0.0, m1 = on ? wholeXY : 0.0, m2 = on ? whole2 : 0.0;
                acc += Pixel(y, p) * m0;
                acc += Pixel(y + 1, p) * m1;
                acc += Pixel(y + 2, p) * m2;
            }
            precise float al = col.last ? wholeY * last : 0.0, bl = col.last ? w0 * last : 0.0, dl = col.last ? last * w2 : 0.0;
            acc += Pixel(y, col.pLast) * al;
            acc += Pixel(y + 1, col.pLast) * bl;
            acc += Pixel(y + 2, col.pLast) * dl;
        }
    }
    else
    {
        // The source rows one after the other, in each the pixel's columns
        int line = y;
        if ((rowFlags & 0x80) != 0)
        {
            precise float whole = wholeX * rowFirst;
            acc = AddRow(acc, line++, col, first, last, rowFirst, whole);
        }
        for (uint k = 0u; k < rowCountWhole; k++)
            acc = AddRow(acc, line++, col, first, last, wholeY, wholeXY);
        if ((rowFlags & 0x40) != 0)
        {
            precise float whole = wholeX * rowLast;
            acc = AddRow(acc, line, col, first, last, rowLast, whole);
        }
    }
    ivec4 v = clamp(ivec4(roundEven(acc)), ivec4(0), ivec4(255));
    pixels[outputWord + r * outputPitch + c] = uint(v.x) | (uint(v.y) << 8) | (uint(v.z) << 16) | (uint(v.w) << 24);
}
