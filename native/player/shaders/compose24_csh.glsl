// 04C4 / 04C5 (ScnVm::Compose, src/scripting/ScnVm.Sprites.cpp) on the GPU mode's compute queue:
// sprites drawn into a 24-bit picture, to the byte. The CPU walks each sprite's rows through their
// run headers (not their pixels) and makes a list of the parts drawn: per sprite row its segments
// (where in the picture's row they start, how many pixels, the run's method, where its data is);
// here one invocation is one 4-byte word of the picture: it reads the word, draws every sprite over
// it in the order of the CPU's (priority, then list order), each byte as the x86 code makes it, and
// writes it back. Every formula of the plain path (0x4409F6) and of blend 0x40000000 (0x440F9C) is
// byte by byte, including where the MMX path takes over from the tables: those depend on the
// byte's place in its run and on the run's address, which the word knows.
//
// The picture's rows start on a word and are whole words long (else the CPU draws), so no word is
// two invocations'.
#version 460

layout (local_size_x = 32, local_size_y = 8) in;

// The picture's words, read and written in place
layout (std430, binding = 0) buffer Target { uint target[]; };
// Per sprite 8 words: first row (of the picture's rectangle), rows, its first entry in Rows, mode
// (0 plain, 1 blend 0x40000000), alpha, the source binding (0-5), the word of its tables T1 and T2
// (blend: 512 bytes) in Sprites, unused
layout (std430, binding = 1) readonly buffer Sprites { uint sprites[]; };
// Per sprite row: its first segment, how many
layout (std430, binding = 2) readonly buffer Rows { uint rows[]; };
// Per segment 4 words: first byte in the row | method << 16, pixels, the data's byte in its source
// binding, unused
layout (std430, binding = 3) readonly buffer Segments { uvec4 segments[]; };
layout (std430, binding = 4) readonly buffer Source0 { uint source0[]; };
layout (std430, binding = 5) readonly buffer Source1 { uint source1[]; };
layout (std430, binding = 6) readonly buffer Source2 { uint source2[]; };
layout (std430, binding = 7) readonly buffer Source3 { uint source3[]; };
layout (std430, binding = 8) readonly buffer Source4 { uint source4[]; };
layout (std430, binding = 9) readonly buffer Source5 { uint source5[]; };

layout (std140, binding = 0) uniform Parameters
{
    // the rectangle: words a row, rows
    int rowWords;
    int rowCount;
    // its first word and the rows' pitch in words in Target
    int targetWord;
    int targetPitch;
    int spriteCount;
    // the script address of the rectangle's first byte and the pitch in bytes, modulo 8 (the MMX
    // path's alignment)
    int addressMod8;
    int pitchMod8;
};

uint SourceWord(uint b, uint i)
{
    switch (b)
    {
    case 0u: return source0[i];
    case 1u: return source1[i];
    case 2u: return source2[i];
    case 3u: return source3[i];
    case 4u: return source4[i];
    default: return source5[i];
    }
}

// The last source word read (its binding and index), for the next bytes of the same word
uint cachedBinding = 0xFFFFFFFFu, cachedIndex = 0xFFFFFFFFu, cachedWord = 0u;

uint SourceByte(uint b, uint at)
{
    uint index = at >> 2;
    if (index != cachedIndex || b != cachedBinding)
    {
        cachedWord = SourceWord(b, index);
        cachedIndex = index;
        cachedBinding = b;
    }
    return (cachedWord >> ((at & 3u) * 8u)) & 0xFFu;
}

uint Table(uint tables, uint v) { return (sprites[tables + (v >> 2)] >> ((v & 3u) * 8u)) & 0xFFu; }

// BlendByte: ((source - d) * alpha >> 8) + d, signed, into a byte
uint BlendByte(uint d, uint s, uint alpha) { return uint(((int(s) - int(d)) * int(alpha) >> 8) + int(d)) & 0xFFu; }

// The new byte d at byte `at` of a segment (method, n pixels, data at src of binding b) whose first
// byte is at script address modulo 8 `address`
uint Draw(uint d, uint at, uint method, uint n, uint b, uint src, uint mode, uint alpha, uint tables, uint address)
{
    uint p = at / 3u, c = at - p * 3u;
    if (mode == 0u)
    {
        if (method == 2u)
            return SourceByte(b, src + at);
        if (method == 3u)
            return SourceByte(b, src + c);
        if (method == 4u)
        {
            uint a = SourceByte(b, src + p * 4u), s = SourceByte(b, src + p * 4u + 1u + c);
            return a == 0xFFu ? s : (s * a + d * (256u - a)) >> 8;
        }
        return BlendByte(d, SourceByte(b, src + 1u + c), SourceByte(b, src));
    }
    // blend 0x40000000: T1 = alpha / 256, T2 = (256 - alpha) / 256 as tables, MMX weights a and 256 - a
    uint t2 = tables + 64u, weight = 256u - alpha;
    if (method == 2u)
    {
        // tables until the destination is 8-byte aligned (some of it in a 4-byte MMX block), then
        // MMX blocks of 8 and one of 4, the rest tables
        uint length = n * 3u, head = (8u - address) & 7u, base = 0u, left = length;
        bool block = false, decided = false;
        if (head != 0u)
        {
            uint h1 = min(head & 3u, length);
            left = length - h1;
            base = h1;
            if (at < h1)
                decided = true;
            else if (left > 0u && left < 4u)
                decided = true;
            else if (left > 0u && (head & 4u) != 0u)
            {
                if (at < h1 + 4u)
                    block = decided = true;
                base = h1 + 4u;
                left -= 4u;
            }
        }
        if (!decided)
        {
            uint groups = left & ~7u;
            if (at < base + groups)
                block = true;
            else if ((left - groups) >= 4u && at < base + groups + 4u)
                block = true;
        }
        uint s = SourceByte(b, src + at);
        if (block)
            return min(255u, ((s * alpha + d * weight) & 0xFFFFu) >> 8);
        return (Table(tables, s) + Table(t2, d)) & 0xFFu;
    }
    if (method == 3u)
    {
        // one colour scaled by a; under 15 pixels all tables, else tables up to an 8-byte aligned
        // pixel, MMX for whole groups of 8 pixels, tables for the rest
        uint colour = (SourceByte(b, src + c) * alpha >> 8) & 0xFFu;
        bool block = false;
        if (n >= 15u)
        {
            uint lead = 0u;
            while (((address + 3u * lead) & 7u) != 0u)
                lead++;
            uint groups = (n - lead) >> 3;
            block = p >= lead && p < lead + groups * 8u;
        }
        if (block)
            return (min(255u, d * weight >> 8) + colour) & 0xFFu;
        return (Table(t2, d) + colour) & 0xFFu;
    }
    if (method == 4u)
    {
        uint a = Table(tables, SourceByte(b, src + p * 4u)), s = SourceByte(b, src + p * 4u + 1u + c);
        if (a == 0xFFu)
            return s;
        if (a == 0u)
            return d;
        return BlendByte(d, s, a);
    }
    return BlendByte(d, SourceByte(b, src + 1u + c), Table(tables, SourceByte(b, src)));
}

void main()
{
    int w = int(gl_GlobalInvocationID.x), y = int(gl_GlobalInvocationID.y);
    if (w >= rowWords || y >= rowCount)
        return;
    uint at = uint(targetWord + y * targetPitch + w);
    uint word = target[at], before = word;
    uint rowAddress = uint(addressMod8 + y * pitchMod8) & 7u;
    uint first = uint(w) * 4u;
    for (int k = 0; k < spriteCount; k++)
    {
        uint e = uint(k) * 8u;
        int top = int(sprites[e]), count = int(sprites[e + 1u]);
        if (y < top || y >= top + count)
            continue;
        uint row = sprites[e + 2u] + uint(y - top);
        uint segment = rows[row * 2u], segmentCount = rows[row * 2u + 1u];
        if (segmentCount == 0u)
            continue;
        uint mode = sprites[e + 3u], alpha = sprites[e + 4u], b = sprites[e + 5u], tables = sprites[e + 6u];
        // the last segment starting at or before the word's first byte (or the first one)
        uint lo = 0u, hi = segmentCount;
        while (hi - lo > 1u)
        {
            uint mid = (lo + hi) >> 1;
            if ((segments[segment + mid].x & 0xFFFFu) <= first)
                lo = mid;
            else
                hi = mid;
        }
        uint i = lo;
        // A word all in one plain copy (method 2): its four bytes from the source as they are
        uvec4 whole = segments[segment + i];
        uint wholeStart = whole.x & 0xFFFFu;
        if (mode == 0u && (whole.x >> 16) == 2u && first >= wholeStart && first + 4u <= wholeStart + whole.y * 3u)
        {
            uint from = whole.z + (first - wholeStart), shift = (from & 3u) * 8u;
            uint low = SourceWord(b, from >> 2);
            word = shift == 0u ? low : (low >> shift) | (SourceWord(b, (from >> 2) + 1u) << (32u - shift));
            continue;
        }
        for (uint k4 = 0u; k4 < 4u; k4++)
        {
            uint byteAt = first + k4;
            while (i + 1u < segmentCount && (segments[segment + i + 1u].x & 0xFFFFu) <= byteAt)
                i++;
            uvec4 s = segments[segment + i];
            uint start = s.x & 0xFFFFu;
            if (byteAt < start || byteAt >= start + s.y * 3u)
                continue;
            uint shift = k4 * 8u;
            uint d = (word >> shift) & 0xFFu;
            uint v = Draw(d, byteAt - start, s.x >> 16, s.y, b, s.z, mode, alpha, tables, (rowAddress + start) & 7u);
            word = (word & ~(0xFFu << shift)) | (v << shift);
        }
    }
    if (word != before)
        target[at] = word;
}
