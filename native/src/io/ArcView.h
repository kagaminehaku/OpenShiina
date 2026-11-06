// An archive file read by offset (C# IO/ArcView.cs). The C# player maps the file into memory; here
// it is read through a cached window of the file, which works on every platform (the Switch's SD
// card has no mapping) and keeps little memory on small devices.
#pragma once

#include <cstdint>
#include <cstdio>
#include <string>

#include "io/Binary.h"

namespace openshiina {

class ArcView
{
public:
    /// Opens the file; throws std::runtime_error when it cannot be opened.
    explicit ArcView(const std::string& path);
    ~ArcView();
    ArcView(const ArcView&) = delete;
    ArcView& operator=(const ArcView&) = delete;

    const std::string& Name() const { return m_name; }
    int64_t MaxOffset() const { return m_length; }

    uint8_t ReadByte(int64_t offset);
    uint32_t ReadUInt32(int64_t offset);
    /// Up to count bytes from offset into buffer (fewer at the end of the file): the bytes read.
    uint32_t Read(int64_t offset, uint8_t* buffer, uint32_t count);
    /// count bytes from offset; past the end of the file they stay 0, as in the C# player.
    Bytes ReadBytes(int64_t offset, uint32_t count);
    bool AsciiEqual(int64_t offset, const char* text);

private:
    void Ensure(int64_t offset, uint32_t size);

    std::string m_name;
    std::FILE* m_file = nullptr;
    int64_t m_length = 0;
    // The cached window: m_window holds the file's bytes from m_windowOffset
    Bytes m_window;
    int64_t m_windowOffset = 0;
};

} // namespace openshiina
