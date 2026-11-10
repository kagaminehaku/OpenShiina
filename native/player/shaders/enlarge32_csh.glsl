// enlarge32 (src/scripting/ScnVm.Enlarge32.cpp) on the GPU mode's compute queue, one invocation a
// destination pixel. The CPU walks the columns once per kind of row (Enlarge32Steps): each
// destination column is a source pixel used whole, times a weight, or two mixed. Every formula is
// the MMX one in 16-bit lanes (pmullw, paddw, psrlw 8; packuswb of lanes that never pass 255).
// Source and destination are words from sourceWord and outputWord, rows sourcePitch and
// outputPitch words apart: the scripts' memory itself, or copies of it.
#version 460

layout (local_size_x = 16, local_size_y = 16) in;

layout (std430, binding = 0) readonly buffer Source { uint source[]; };
// Per destination row: kind (0 a partly covered row from one source row, 1 one from two, 2 a whole
// row from one, 3 from two), its source row, the weight (kind 0) or the upper row's, the lower
// row's (each 64 bits: low word, high word), two unused
layout (std430, binding = 1) readonly buffer Rows { uint rows[]; };
// Per destination column: what (0 whole, 1 weighted, 2 mixed), its source pixel on rows from one
// source row, on rows from two, the other pixel of a mix, the two weights (64 bits each)
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
};

uvec4 Lanes(uint low, uint high) { return uvec4(low & 0xFFFFu, low >> 16, high & 0xFFFFu, high >> 16); }

uvec4 Mul(uvec4 a, uvec4 b) { return (a * b) & 0xFFFFu; }

uvec4 Add(uvec4 a, uvec4 b) { return (a + b) & 0xFFFFu; }

uvec4 Unpack(int line, int x)
{
    uint p = source[sourceWord + line * sourcePitch + x];
    return uvec4(p & 0xFFu, (p >> 8) & 0xFFu, (p >> 16) & 0xFFu, p >> 24);
}

void main()
{
    int c = int(gl_GlobalInvocationID.x), r = int(gl_GlobalInvocationID.y);
    if (c >= columnCount || r >= rowCount)
        return;
    uint kind = rows[r * 8];
    int line = int(rows[r * 8 + 1]);
    uvec4 a = Lanes(rows[r * 8 + 2], rows[r * 8 + 3]), lower = Lanes(rows[r * 8 + 4], rows[r * 8 + 5]);
    bool two = kind == 1u || kind == 3u, weighted = kind == 0u;
    uint what = columns[c * 8];
    int cur = int(columns[c * 8 + (two ? 2 : 1)]), next = int(columns[c * 8 + 3]);
    uvec4 w0 = Lanes(columns[c * 8 + 4], columns[c * 8 + 5]), w1 = Lanes(columns[c * 8 + 6], columns[c * 8 + 7]);

    // The source pixel as the row sees it: one row unpacked, or the two rows mixed (>> 8)
    uvec4 p = two ? Add(Mul(Unpack(line, cur), a), Mul(Unpack(line + 1, cur), lower)) >> 8u : Unpack(line, cur);
    uvec4 v;
    if (what == 0u)
        v = p;
    else
    {
        uvec4 sum = Mul(w0, p);
        if (what == 2u)
        {
            uvec4 q = two ? Add(Mul(Unpack(line, next), a), Mul(Unpack(line + 1, next), lower)) >> 8u : Unpack(line, next);
            sum = Add(sum, Mul(w1, q));
        }
        v = sum >> 8u;
    }
    // the row's weight for the partly covered rows from one source row
    if (weighted)
        v = Mul(v, a) >> 8u;
    pixels[outputWord + r * outputPitch + c] = (v.x & 0xFFu) | ((v.y & 0xFFu) << 8) | ((v.z & 0xFFu) << 16) | ((v.w & 0xFFu) << 24);
}
