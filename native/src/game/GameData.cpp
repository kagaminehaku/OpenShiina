// C# Game/GameData.cs
#include "game/GameData.h"

#include <algorithm>
#include <regex>

#include "archives/FormatManager.h"
#include "platform/Files.h"
#include "text/Text.h"

namespace openshiina::game {

using archives::EncryptionScheme;
using archives::WarcArchive;
using archives::WarcOpener;

namespace {

std::string Fold(const std::string& text) { return Text::FoldCase(text); }

std::string Trim(const std::string& text)
{
    size_t a = text.find_first_not_of(" \t\r\n\v\f"), b = text.find_last_not_of(" \t\r\n\v\f");
    return a == std::string::npos ? std::string() : text.substr(a, b - a + 1);
}

/// Path.GetFileName: after the last '\' or '/'.
std::string FileName(const std::string& path)
{
    size_t at = path.find_last_of("\\/");
    return at == std::string::npos ? path : path.substr(at + 1);
}

/// Path.GetExtension: from the last '.' of the file name ("" when none, or when it ends the name).
std::string Extension(const std::string& path)
{
    std::string name = FileName(path);
    size_t dot = name.rfind('.');
    return dot == std::string::npos || dot == name.size() - 1 ? std::string() : name.substr(dot);
}

/// Path.GetFileNameWithoutExtension of the trimmed path, '\' taken as '/'.
std::string Stem(const std::string& path)
{
    std::string name = FileName(Trim(path));
    size_t dot = name.rfind('.');
    return dot == std::string::npos ? name : name.substr(0, dot);
}

std::vector<std::string> Parts(const std::string& path)
{
    std::vector<std::string> parts;
    size_t start = 0;
    while (start <= path.size())
    {
        size_t end = path.find_first_of("\\/", start);
        std::string part = path.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (!part.empty())
            parts.push_back(part);
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    return parts;
}

std::vector<std::string> Sorted(std::vector<std::string> names)
{
    std::stable_sort(names.begin(), names.end(), [](const std::string& a, const std::string& b) { return Text::LessIgnoreCase(a, b); });
    return names;
}

bool IsEngineIni(const std::string& path)
{
    Bytes bytes;
    if (!Files::ReadAll(path, bytes))
        return false;
    std::string text = Text::Cp932ToUtf8(bytes.data(), bytes.size());
    size_t start = 0;
    while (start < text.size())
    {
        size_t end = text.find('\n', start);
        std::string line = Trim(text.substr(start, end == std::string::npos ? std::string::npos : end - start));
        if (!line.empty() && line[0] != ';')
            return line.rfind("[椎名里緒", 0) == 0;
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    return false;
}

} // namespace

std::unique_ptr<GameData> GameData::Open(const std::string& folder, std::shared_ptr<const EncryptionScheme> scheme)
{
    std::unique_ptr<GameData> data(new GameData(folder, scheme->Name));
    std::vector<std::string> wars;
    for (const auto& name : Files::FileNames(folder))
        if (Text::EqualsIgnoreCase(Extension(name), ".war"))
            wars.push_back(name);
    for (const auto& war : Sorted(wars))
    {
        std::unique_ptr<WarcArchive> warc;
        try
        {
            warc = WarcOpener::TryOpen(std::make_unique<ArcView>(Files::Combine(folder, war)), scheme);
        }
        catch (const std::exception&)
        {
            warc = nullptr;
        }
        if (!warc)
            continue;
        for (const auto& entry : warc->Entries)
        {
            std::string stem = Fold(Stem(entry.Name));
            auto& list = data->m_byStem[stem];
            if (list.empty())
                data->m_stems.push_back(stem);
            list.push_back({warc.get(), &entry});
        }
        data->m_archives.push_back(std::move(warc));
    }
    for (const auto& [stem, list] : data->m_byStem)
        if (list.size() > 1)
            for (const auto& candidate : list)
                data->m_shared.insert(candidate.Archive);
    if (data->m_archives.empty())
        throw InvalidDataError("No archive in " + folder + " could be opened with the " + scheme->Name + " scheme.");
    // The engine mounts the archive RIO.INI names (Arc=) before the first script runs, so its
    // files come first
    if (auto ini = EngineIni(folder))
    {
        Bytes bytes;
        if (Files::ReadAll(*ini, bytes))
        {
            std::string text = Text::Cp932ToUtf8(bytes.data(), bytes.size());
            static const std::regex arc(R"((?:^|\n)Arc=([^\n]+))", std::regex::icase);
            std::smatch match;
            if (std::regex_search(text, match, arc))
                data->Mount(Trim(match[1].str()), true);
        }
    }
    return data;
}

std::optional<std::string> GameData::FindArchive(const std::string& folder)
{
    if (!Files::IsDirectory(folder))
        return std::nullopt;
    std::vector<std::string> wars;
    for (const auto& name : Files::FileNames(folder))
        if (Text::EqualsIgnoreCase(Extension(name), ".war"))
            wars.push_back(name);
    if (wars.empty())
        return std::nullopt;
    return Files::Combine(folder, Sorted(wars).front());
}

std::optional<std::string> GameData::ResolvePath(const std::string& folder, const std::string& path)
{
    std::string current = folder;
    auto parts = Parts(path);
    for (size_t i = 0; i < parts.size(); i++)
    {
        const std::string& part = parts[i];
        if (part == "." || part == ".." || part.find(':') != std::string::npos)
            return std::nullopt;
        std::string exact = Files::Combine(current, part);
        bool last = i == parts.size() - 1;
        if (last ? Files::IsFile(exact) : Files::IsDirectory(exact))
        {
            current = exact;
            continue;
        }
        if (!Files::IsDirectory(current))
            return std::nullopt;
        std::optional<std::string> match;
        for (const auto& name : last ? Files::FileNames(current) : Files::FolderNames(current))
            if (Text::EqualsIgnoreCase(name, part))
            {
                match = Files::Combine(current, name);
                break;
            }
        if (!match)
            return std::nullopt;
        current = *match;
    }
    return parts.empty() ? std::nullopt : std::optional<std::string>(current);
}

std::optional<std::string> GameData::EngineIni(const std::string& folder)
{
    if (auto rio = ResolvePath(folder, "RIO.INI"))
        return rio;
    if (!Files::IsDirectory(folder))
        return std::nullopt;
    std::vector<std::string> exes, inis;
    for (const auto& name : Files::FileNames(folder))
    {
        if (Text::EqualsIgnoreCase(Extension(name), ".exe"))
            exes.push_back(Fold(Stem(name)));
        else if (Text::EqualsIgnoreCase(Extension(name), ".ini") && IsEngineIni(Files::Combine(folder, name)))
            inis.push_back(name);
    }
    inis = Sorted(inis);
    std::stable_sort(inis.begin(), inis.end(), [&](const std::string& a, const std::string& b) {
        auto rank = [&](const std::string& n) { return std::find(exes.begin(), exes.end(), Fold(Stem(n))) != exes.end() ? 0 : 1; };
        return rank(a) < rank(b);
    });
    if (inis.empty())
        return std::nullopt;
    return Files::Combine(folder, inis.front());
}

bool GameData::Mount(const std::string& archive, bool mounted)
{
    WarcArchive* warc = nullptr;
    std::string name = FileName(Trim(archive));
    for (const auto& a : m_archives)
        if (Text::EqualsIgnoreCase(FileName(a->File->Name()), name))
        {
            warc = a.get();
            break;
        }
    if (!warc)
        return false;
    bool there = std::find(m_mounted.begin(), m_mounted.end(), warc) != m_mounted.end();
    if (there == mounted)
        return false;
    if (mounted)
        m_mounted.push_back(warc);
    else
        m_mounted.erase(std::find(m_mounted.begin(), m_mounted.end(), warc));
    return m_shared.count(warc) != 0;
}

template <typename F> std::optional<GameData::Candidate> GameData::Pick(const std::vector<Candidate>& list, F match)
{
    std::optional<Candidate> best;
    size_t bestOrder = SIZE_MAX;
    for (const auto& candidate : list)
        if (match(candidate))
        {
            auto at = std::find(m_mounted.begin(), m_mounted.end(), candidate.Archive);
            size_t order = at == m_mounted.end() ? SIZE_MAX - 1 : size_t(at - m_mounted.begin());
            if (order < bestOrder)
            {
                best = candidate;
                bestOrder = order;
            }
        }
    return best;
}

std::optional<GameData::Candidate> GameData::Find(const std::string& path, std::initializer_list<const char*> extensions)
{
    if (Trim(path).empty())
        return std::nullopt;
    auto found = m_byStem.find(Fold(Stem(path)));
    if (found == m_byStem.end())
        return std::nullopt;
    const auto& list = found->second;
    if (list.size() == 1)
        return list[0];
    std::string ext = Extension(Trim(path));
    if (auto c = Pick(list, [&](const Candidate& c) { return Text::EqualsIgnoreCase(Extension(c.Entry->Name), ext); }))
        return c;
    if (auto c = Pick(list, [&](const Candidate& c) {
            for (const char* e : extensions)
                if (Text::EqualsIgnoreCase(Extension(c.Entry->Name), e))
                    return true;
            return false;
        }))
        return c;
    return Pick(list, [](const Candidate&) { return true; });
}

bool GameData::Read(const std::string& path, Bytes& data, std::initializer_list<const char*> extensions)
{
    auto found = Find(path, extensions);
    if (!found)
        return false;
    data = WarcOpener::OpenEntry(*found->Archive, *found->Entry);
    return true;
}

std::optional<int64_t> GameData::Size(const std::string& path, std::initializer_list<const char*> extensions)
{
    auto found = Find(path, extensions);
    if (!found)
        return std::nullopt;
    return int64_t(found->Entry->UnpackedSize);
}

std::optional<int64_t> GameData::Size(const std::string& archive, const std::string& path)
{
    if (Trim(path).empty())
        return std::nullopt;
    auto found = m_byStem.find(Fold(Stem(path)));
    if (found == m_byStem.end())
        return std::nullopt;
    std::string ext = Extension(Trim(path));
    for (const auto& c : found->second)
        if (Text::EqualsIgnoreCase(FileName(c.Archive->File->Name()), Trim(archive))
            && (ext.empty() || Text::EqualsIgnoreCase(Extension(c.Entry->Name), ext)))
            return int64_t(c.Entry->UnpackedSize);
    return std::nullopt;
}

bool GameData::ExecutableCopy(std::optional<int64_t> exeSize, Bytes& data)
{
    if (m_executableSearched)
    {
        if (m_executableFound)
            data = m_executableCopy;
        return m_executableFound;
    }
    m_executableSearched = true;
    std::vector<Candidate> candidates;
    for (const auto& stem : m_stems)
        for (const auto& c : m_byStem[stem])
        {
            int64_t size = c.Entry->UnpackedSize;
            if (size >= 0x10000 && size <= 0x1000000 && (!exeSize || (size >= *exeSize / 2 && size <= *exeSize * 2)))
                candidates.push_back(c);
        }
    std::stable_sort(candidates.begin(), candidates.end(), [&](const Candidate& a, const Candidate& b) {
        auto key = [&](const Candidate& c) {
            return exeSize ? std::abs(int64_t(c.Entry->UnpackedSize) - *exeSize) : int64_t(c.Archive->Entries.size());
        };
        return key(a) < key(b);
    });
    if (candidates.size() > 64)
        candidates.resize(64);
    for (const auto& c : candidates)
    {
        Bytes bytes;
        try
        {
            bytes = WarcOpener::OpenEntry(*c.Archive, *c.Entry);
        }
        catch (const std::exception&)
        {
            continue;
        }
        if (bytes.size() > 0x40 && bytes[0] == 'M' && bytes[1] == 'Z')
        {
            int pe = int(LittleEndian::ToUInt32(&bytes[0x3C]));
            if (pe > 0 && pe <= int(bytes.size()) - 4 && std::memcmp(&bytes[size_t(pe)], "PE\0\0", 4) == 0)
            {
                m_executableCopy = bytes;
                m_executableFound = true;
                data = bytes;
                return true;
            }
        }
    }
    return false;
}

std::optional<std::string> GameData::LooseFile(const std::string& path)
{
    if (auto listed = ResolveListed(path))
        return listed;
    if (Text::EqualsIgnoreCase(path, "RIO.INI"))
        return EngineIni(m_folder);
    return std::nullopt;
}

std::optional<std::string> GameData::ResolveListed(const std::string& path)
{
    std::string current = m_folder;
    auto parts = Parts(path);
    for (size_t i = 0; i < parts.size(); i++)
    {
        const std::string& part = parts[i];
        if (part == "." || part == ".." || part.find(':') != std::string::npos)
            return std::nullopt;
        // A folder is listed once a session (none when it is not there)
        auto known = m_listings.find(current);
        if (known == m_listings.end())
        {
            std::optional<Listing> listing;
            if (Files::IsDirectory(current))
            {
                listing.emplace();
                for (const auto& name : Files::FileNames(current))
                    listing->Files.emplace(Fold(name), Files::Combine(current, name));
                for (const auto& name : Files::FolderNames(current))
                    listing->Folders.emplace(Fold(name), Files::Combine(current, name));
            }
            known = m_listings.emplace(current, std::move(listing)).first;
        }
        if (!known->second)
            return std::nullopt;
        const auto& listing = *known->second;
        const auto& names = i == parts.size() - 1 ? listing.Files : listing.Folders;
        auto next = names.find(Fold(part));
        if (next == names.end())
            return std::nullopt;
        current = next->second;
    }
    return parts.empty() ? std::nullopt : std::optional<std::string>(current);
}

} // namespace openshiina::game
