// The index of WARC 1.2-1.6 archives is packed with a range coder: Michael Schindler's (32-bit
// arithmetic, renormalised a byte at a time, seven bits of the first byte to start with) over 257
// symbols - the 256 byte values and an end mark - whose frequencies a quasistatic model adapts as
// the data goes (C# Archives/WarcRangeDecoder.cs, where the method is described).
#pragma once

#include <cstddef>
#include <cstdint>

namespace openshiina::archives::WarcRangeDecoder {

/// Unpacks input into output up to the end mark (or the end of the input); the bytes written, 0
/// when they do not fit.
size_t Decode(const uint8_t* input, size_t inputLength, uint8_t* output, size_t outputLength);

} // namespace openshiina::archives::WarcRangeDecoder
