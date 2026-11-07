// What the embedded routines' C++ versions test before they work in place (C# ScnVm.NativeRoutines.cs's
// Apart and RowsSpan): whether blocks of script memory share bytes.
#pragma once

#include <algorithm>
#include <cstdint>

namespace openshiina::scripting {

struct Span64
{
    int64_t Start, Length;
};

/// Two blocks of memory share no byte.
inline bool Apart(int64_t a, int64_t aLength, int64_t b, int64_t bLength) { return a + aLength <= b || b + bLength <= a; }
inline bool Apart(Span64 a, Span64 b) { return Apart(a.Start, a.Length, b.Start, b.Length); }

/// The bytes rows of rowBytes at start + k * stride (k below rows) lie in.
inline Span64 RowsSpan(int start, int stride, int rows, int64_t rowBytes)
{
    int64_t first = start, last = start + int64_t(stride) * (rows - 1);
    int64_t low = std::min(first, last);
    return {low, std::max(first, last) + rowBytes - low};
}

} // namespace openshiina::scripting
