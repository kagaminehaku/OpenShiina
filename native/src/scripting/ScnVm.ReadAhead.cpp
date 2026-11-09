// C# Scripting/ScnVm.ReadAhead.cs: reading ahead. The pictures and sounds a scenario names are read
// from the archives on reader threads (BackgroundReader) a few files before the scripts ask for
// them: decrypting and unpacking a picture is most of the time of 04B0, and the scripts load them
// one after another as a scene starts, which held the frame. Each scenario file (.TXT) the scripts
// read is searched for names of the archives' pictures and sounds, in the order they come (read by
// any opcode, or by the engine's own background loader 00CE); when the scripts read one of them,
// the next few are read ahead. A file read ahead is the same bytes the scripts would have read:
// only when it is ready changes. With LoadInBackground (the player; scnrun: SCNRUN_BGLOAD=1).
// While the game skips, its voices are not read ahead: once three sounds of a folder read ahead
// were passed by unread, that folder's sounds wait until the scripts read one of them again.
//
// The C# player finds the names with a regular expression on the decoded text whose \b ends a
// name before a character that is not a letter or digit in Unicode's sense; here the end is
// looked for by hand, Japanese letters counted as letters and its punctuation not. Which files
// are read ahead only changes how soon they are ready.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <numeric>

#include "text/Text.h"

namespace openshiina::scripting {

namespace {

// How many of a scenario's next files are read ahead
constexpr int AheadFiles = 6;
// The reader threads
constexpr int ReaderThreads = 2;

bool IsNameChar(char c)
{
    return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-';
}

// The code point at text[at] (UTF-8)
char32_t CodePointAt(const std::string& text, size_t at)
{
    auto b = uint8_t(text[at]);
    int n = b < 0x80 ? 1 : b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
    char32_t c = n == 1 ? b : n == 2 ? b & 0x1F : n == 3 ? b & 0x0F : b & 0x07;
    for (int k = 1; k < n && at + size_t(k) < text.size(); k++)
        c = c << 6 | (uint8_t(text[at + size_t(k)]) & 0x3F);
    return c;
}

// A letter, digit or connector of .NET's \w: ASCII ones, and past ASCII anything but spaces,
// punctuation and symbols of Japanese text
bool IsWordChar(char32_t c)
{
    if (c < 0x80)
        return IsNameChar(char(c)) && c != '-';
    if (c == 0x3005 || c == 0x3006)
        return true; // 々 〆
    if ((c >= 0x3000 && c <= 0x303F) || (c >= 0x2000 && c <= 0x206F) || c == 0x30FB || (c >= 0xFF01 && c <= 0xFF0F)
        || (c >= 0xFF1A && c <= 0xFF20) || (c >= 0xFF3B && c <= 0xFF40) || (c >= 0xFF5B && c <= 0xFF65) || c == 0xA0
        || (c >= 0x2190 && c <= 0x2BFF) || (c >= 0x25A0 && c <= 0x27BF))
        return false;
    return true;
}

bool EndsWithIgnoreCase(const std::string& name, const char* ending)
{
    size_t n = std::char_traits<char>::length(ending);
    return name.size() >= n && Text::EqualsIgnoreCase(std::string_view(name).substr(name.size() - n), ending);
}

bool IsSound(const std::string& name) { return EndsWithIgnoreCase(name, ".ogg") || EndsWithIgnoreCase(name, ".wav"); }

std::string FolderOf(const std::string& name)
{
    size_t at = name.find_last_of("\\/");
    return at == std::string::npos ? std::string() : SjisFold(name.substr(0, at));
}

// The names of pictures and sounds in a scenario, in order: "e\eva01.s25", "st\hm10_m.s25",
// "se01.ogg" ([0-9A-Za-z_\-]+(?:\\[0-9A-Za-z_\-]+)*\.(?:s25|ogg|wav)\b)
std::vector<std::string> MediaNames(const std::string& text)
{
    std::vector<std::string> names;
    size_t at = 0;
    while (at < text.size())
    {
        // A match starts where a run of name characters starts
        if (!IsNameChar(text[at]) || (at > 0 && IsNameChar(text[at - 1])))
        {
            at++;
            continue;
        }
        size_t end = at;
        while (end < text.size() && IsNameChar(text[end]))
            end++;
        while (end + 1 < text.size() && text[end] == '\\' && IsNameChar(text[end + 1]))
        {
            end++;
            while (end < text.size() && IsNameChar(text[end]))
                end++;
        }
        bool found = false;
        if (end + 4 <= text.size() && text[end] == '.')
        {
            std::string extension = text.substr(end + 1, 3);
            if (Text::EqualsIgnoreCase(extension, "s25") || Text::EqualsIgnoreCase(extension, "ogg") || Text::EqualsIgnoreCase(extension, "wav"))
                found = end + 4 == text.size() || !IsWordChar(CodePointAt(text, end + 4));
        }
        if (found)
        {
            names.push_back(text.substr(at, end + 4 - at));
            at = end + 4;
        }
        else
            at++;
    }
    return names;
}

} // namespace

std::shared_ptr<BackgroundRead> ScnVm::ReadInBackground(const std::string& name, bool first)
{
    if (!m_reader)
        m_reader = std::make_unique<BackgroundReader>(ReaderThreads);
    return m_reader->Post([this, name](openshiina::Bytes& data) { return ReadArchiveFile(name, data); }, first);
}

std::shared_ptr<BackgroundRead> ScnVm::TakeAheadRead(const std::string& name)
{
    auto found = m_ahead.find(SjisFold(name));
    if (found == m_ahead.end())
        return nullptr;
    auto read = std::move(found->second.first);
    m_ahead.erase(found);
    m_aheadUsed++;
    return read;
}

bool ScnVm::TakeAhead(const std::string& name, openshiina::Bytes& data)
{
    auto read = TakeAheadRead(name);
    return read && read->Wait(data);
}

void ScnVm::LearnScenario(const std::string& name, const openshiina::Bytes& text)
{
    // The scenario being played, read again, goes on where it was
    if (!LoadInBackground || !ReadAhead || SjisFold(name) == m_scenarioName)
        return;
    m_scenarioName = SjisFold(name);
    m_scenarioFiles.clear();
    m_scenarioOrder.clear();
    for (const auto& file : MediaNames(Text::Cp932ToUtf8(text.data(), text.size())))
    {
        std::string key = SjisFold(file);
        if (m_scenarioOrder.count(key))
            continue;
        auto size = m_host.ArchiveFileSize(file);
        if (!size || *size > FileCacheBytes / 2)
            continue;
        m_scenarioOrder[key] = int(m_scenarioFiles.size());
        m_scenarioFiles.push_back(file);
    }
    // Reads ahead for the scenario before this one are let go
    std::erase_if(m_ahead, [this](const auto& a) { return !m_scenarioOrder.count(a.first); });
    StartAhead(0);
}

void ScnVm::ReadAheadAfter(const std::string& name)
{
    if (!LoadInBackground || !ReadAhead)
        return;
    auto at = m_scenarioOrder.find(SjisFold(name));
    if (at == m_scenarioOrder.end())
        return;
    if (IsSound(name))
        m_soundsPassed.erase(FolderOf(name));
    StartAhead(at->second + 1);
}

bool ScnVm::Skipped(const std::string& name)
{
    if (!IsSound(name))
        return false;
    auto passed = m_soundsPassed.find(FolderOf(name));
    return passed != m_soundsPassed.end() && passed->second >= 3;
}

void ScnVm::StartAhead(int from)
{
    // Files passed by without being read (a branch not taken, a sound skipped) are let go
    for (auto a = m_ahead.begin(); a != m_ahead.end();)
    {
        auto order = m_scenarioOrder.find(a->first);
        if ((order == m_scenarioOrder.end() ? -1 : order->second) < from - AheadFiles)
        {
            if (order != m_scenarioOrder.end() && IsSound(m_scenarioFiles[size_t(order->second)]))
                m_soundsPassed[FolderOf(m_scenarioFiles[size_t(order->second)])]++;
            a = m_ahead.erase(a);
        }
        else
            ++a;
    }
    int64_t waiting = std::accumulate(m_ahead.begin(), m_ahead.end(), int64_t(0), [](int64_t sum, const auto& a) { return sum + a.second.second; });
    // The next AheadFiles files, but sounds of a folder the game skips
    int taken = 0;
    int last = std::min(from + 4 * AheadFiles, int(m_scenarioFiles.size()));
    for (int k = from; taken < AheadFiles && k < last; k++)
    {
        const std::string& name = m_scenarioFiles[size_t(k)];
        if (Skipped(name))
            continue;
        taken++;
        std::string key = SjisFold(name);
        if (m_fileCache.count(key) || m_ahead.count(key))
            continue;
        auto size = m_host.ArchiveFileSize(name);
        if (!size)
            continue;
        if (waiting + *size > FileCacheBytes / 2)
            break;
        waiting += *size;
        m_aheadRead++;
        m_ahead[key] = {ReadInBackground(name, false), *size};
    }
}

} // namespace openshiina::scripting
