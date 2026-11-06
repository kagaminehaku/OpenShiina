// fseeko / ftello with 64-bit offsets (archives over 2 GB on 32-bit devices): POSIX, which a strict
// C++20 build leaves out unless asked for
#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#define _FILE_OFFSET_BITS 64
#endif

#include "io/ArcView.h"

#include <cstring>
#include <stdexcept>

#include "platform/Files.h"

namespace openshiina {

namespace {

constexpr uint32_t kWindowSize = 65536;

bool Seek(std::FILE* file, int64_t offset)
{
#ifdef _WIN32
    return _fseeki64(file, offset, SEEK_SET) == 0;
#else
    return fseeko(file, off_t(offset), SEEK_SET) == 0;
#endif
}

int64_t Length(std::FILE* file)
{
#ifdef _WIN32
    if (_fseeki64(file, 0, SEEK_END) != 0) return -1;
    return _ftelli64(file);
#else
    if (fseeko(file, 0, SEEK_END) != 0) return -1;
    return int64_t(ftello(file));
#endif
}

} // namespace

ArcView::ArcView(const std::string& path)
    : m_name(path)
{
    m_file = Files::Open(path, "rb");
    if (!m_file)
        throw std::runtime_error("Cannot open " + path);
    m_length = Length(m_file);
    if (m_length < 0)
    {
        std::fclose(m_file);
        throw std::runtime_error("Cannot read " + path);
    }
}

ArcView::~ArcView()
{
    if (m_file)
        std::fclose(m_file);
}

void ArcView::Ensure(int64_t offset, uint32_t size)
{
    if (offset >= m_windowOffset && offset + size <= m_windowOffset + int64_t(m_window.size()))
        return;
    int64_t start = offset < 0 ? 0 : offset;
    uint32_t want = size > kWindowSize ? size : kWindowSize;
    if (start + want > m_length)
        want = uint32_t(m_length > start ? m_length - start : 0);
    m_window.resize(want);
    m_windowOffset = start;
    size_t got = want != 0 && Seek(m_file, start) ? std::fread(m_window.data(), 1, want, m_file) : 0;
    m_window.resize(got);
}

uint8_t ArcView::ReadByte(int64_t offset)
{
    Ensure(offset, 1);
    if (offset < m_windowOffset || offset >= m_windowOffset + int64_t(m_window.size()))
        throw InvalidDataError("Read past the end of " + m_name);
    return m_window[size_t(offset - m_windowOffset)];
}

uint32_t ArcView::ReadUInt32(int64_t offset)
{
    uint8_t b[4];
    if (Read(offset, b, 4) != 4)
        throw InvalidDataError("Read past the end of " + m_name);
    return LittleEndian::ToUInt32(b);
}

uint32_t ArcView::Read(int64_t offset, uint8_t* buffer, uint32_t count)
{
    if (offset < 0 || offset >= m_length || count == 0)
        return 0;
    if (int64_t(count) > m_length - offset)
        count = uint32_t(m_length - offset);
    // Large reads go straight to the buffer; small ones through the window
    if (count >= kWindowSize)
    {
        if (!Seek(m_file, offset))
            return 0;
        return uint32_t(std::fread(buffer, 1, count, m_file));
    }
    Ensure(offset, count);
    int64_t available = m_windowOffset + int64_t(m_window.size()) - offset;
    if (available <= 0)
        return 0;
    uint32_t n = available < count ? uint32_t(available) : count;
    std::memcpy(buffer, m_window.data() + (offset - m_windowOffset), n);
    return n;
}

Bytes ArcView::ReadBytes(int64_t offset, uint32_t count)
{
    Bytes data(count);
    Read(offset, data.data(), count);
    return data;
}

bool ArcView::AsciiEqual(int64_t offset, const char* text)
{
    size_t length = std::strlen(text);
    if (offset + int64_t(length) > m_length)
        return false;
    Bytes head(length);
    if (Read(offset, head.data(), uint32_t(length)) != length)
        return false;
    return std::memcmp(head.data(), text, length) == 0;
}

} // namespace openshiina
