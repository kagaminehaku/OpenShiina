// The Switch player's list of games (see GameList.h)
#include "GameList.h"

#include <sys/stat.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <map>

#include "archives/FormatManager.h"
#include "archives/WarcOpener.h"
#include "game/GameData.h"
#include "platform/Files.h"
#include "text/Text.h"

namespace openshiina::player {

namespace {

int64_t FolderTime(const std::string& path)
{
    struct stat info;
    return stat(path.c_str(), &info) == 0 ? int64_t(info.st_mtime) : 0;
}

std::string Lower(std::string text)
{
    std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return char(c >= 'A' && c <= 'Z' ? c + 32 : c); });
    return text;
}

// The game's own .exe for the icon, when the folder has one (C# GameLibrary.MainExe: the largest,
// installers apart)
std::string MainExe(const std::string& path)
{
    std::string best;
    int64_t bestSize = -1;
    for (const auto& name : Files::FileNames(path))
    {
        std::string lower = Lower(name);
        if (!lower.ends_with(".exe") || lower.starts_with("unins") || lower.starts_with("setup"))
            continue;
        struct stat info;
        std::string file = Files::Combine(path, name);
        if (stat(file.c_str(), &info) == 0 && int64_t(info.st_size) > bestSize)
        {
            best = file;
            bestSize = int64_t(info.st_size);
        }
    }
    return best;
}

// C# GameLibrary.Identify: the scheme the archives and the .exe tell, else the one chosen for it;
// its name, its .exe
GameEntry Identify(const std::string& path, bool internal, const std::string& chosen)
{
    GameEntry game{path, Files::FileName(path), {}, false, internal, FolderTime(path), 0, 0, {}};
    auto archive = game::GameData::FindArchive(path);
    if (!archive)
        return game;
    auto* formats = archives::FormatManager::Instance();
    if (auto scheme = formats->SchemeForArchive(*archive))
    {
        game.Known = true;
        game.Name = scheme->Name;
        for (const auto& [file, name] : formats->GameMap().Items())
            if (name == scheme->Name)
                if (auto exe = game::GameData::ResolvePath(path, file))
                {
                    game.Exe = *exe;
                    break;
                }
        return game;
    }
    if (auto scheme = chosen.empty() ? nullptr : formats->GetScheme(chosen))
    {
        game.Known = true;
        game.Name = scheme->Name;
        game.Exe = MainExe(path);
    }
    return game;
}

// The fields of each line of a .tsv
std::vector<std::vector<std::string>> ReadTsv(const std::string& file)
{
    std::vector<std::vector<std::string>> lines;
    Bytes data;
    if (!Files::ReadAll(file, data))
        return lines;
    std::string text(data.begin(), data.end());
    size_t start = 0;
    while (start < text.size())
    {
        size_t end = text.find('\n', start);
        if (end == std::string::npos)
            end = text.size();
        std::vector<std::string> fields;
        for (size_t at = start; at <= end;)
        {
            size_t tab = text.find('\t', at);
            if (tab == std::string::npos || tab > end)
                tab = end;
            fields.push_back(text.substr(at, tab - at));
            at = tab + 1;
        }
        lines.push_back(std::move(fields));
        start = end + 1;
    }
    return lines;
}

void WriteText(const std::string& file, const std::string& text)
{
    if (std::FILE* f = Files::Open(file, "wb"))
    {
        std::fwrite(text.data(), 1, text.size(), f);
        std::fclose(f);
    }
}

// played.tsv: a game a line, its folder's name, when it was played last, its seconds, the scheme
// chosen for it, by tabs
void WriteRecords(const std::string& file, const std::map<std::string, GameRecord>& records)
{
    std::string text;
    for (const auto& [folder, r] : records)
        text += folder + '\t' + std::to_string(r.LastPlayed) + '\t' + std::to_string(r.PlaySeconds) + '\t' + r.Scheme + '\n';
    WriteText(file, text);
}

// library.tsv: a game a line, its folder, time, known (1 / 0), name and .exe, by tabs
std::map<std::string, GameEntry> ReadCache(const std::string& cache)
{
    std::map<std::string, GameEntry> games;
    for (const auto& fields : ReadTsv(cache))
        if (fields.size() == 5)
        {
            GameEntry game{fields[0], fields[3], fields[4], fields[2] == "1", false, std::strtoll(fields[1].c_str(), nullptr, 10), 0, 0, {}};
            games[game.Path] = game;
        }
    return games;
}

void WriteCache(const std::string& cache, const std::vector<GameEntry>& games)
{
    std::string text;
    for (const auto& g : games)
        text += g.Path + '\t' + std::to_string(g.Stamp) + '\t' + (g.Known ? "1" : "0") + '\t' + g.Name + '\t' + g.Exe + '\n';
    WriteText(cache, text);
}

// The first bytes a file of that name has once decoded, or empty for one without such a start
std::string KnownStart(const std::string& name)
{
    std::string lower = Lower(name);
    if (lower.ends_with(".s25"))
        return std::string("S25\0", 4);
    if (lower.ends_with(".ogg"))
        return "OggS";
    if (lower.ends_with(".wav"))
        return "RIFF";
    return {};
}

} // namespace

std::map<std::string, GameRecord> ReadRecords(const std::string& file)
{
    std::map<std::string, GameRecord> records;
    for (const auto& fields : ReadTsv(file))
        if (fields.size() == 4)
            records[fields[0]] = {std::strtoll(fields[1].c_str(), nullptr, 10), std::strtoll(fields[2].c_str(), nullptr, 10), fields[3]};
    return records;
}

void RecordPlay(const std::string& file, const std::string& folder, int64_t seconds)
{
    auto records = ReadRecords(file);
    auto& r = records[folder];
    r.LastPlayed = int64_t(std::time(nullptr));
    r.PlaySeconds += std::max<int64_t>(seconds, 0);
    WriteRecords(file, records);
}

void ChooseScheme(const std::string& file, const std::string& folder, const std::string& scheme)
{
    auto records = ReadRecords(file);
    records[folder].Scheme = scheme;
    WriteRecords(file, records);
}

void ForgetGame(const std::string& file, const std::string& folder)
{
    auto records = ReadRecords(file);
    if (records.erase(folder) != 0)
        WriteRecords(file, records);
}

std::vector<GameEntry> ListGames(const std::vector<std::pair<std::string, bool>>& roots, const std::string& cache, const std::string& records,
                                 bool again)
{
    auto known = again ? std::map<std::string, GameEntry>() : ReadCache(cache);
    auto played = ReadRecords(records);
    std::vector<GameEntry> games;
    bool changed = again;
    for (const auto& [root, internal] : roots)
        for (const auto& folder : Files::FolderNames(root))
        {
            std::string path = Files::Combine(root, folder);
            auto record = played.find(folder);
            std::string chosen = record != played.end() ? record->second.Scheme : std::string();
            auto found = known.find(path);
            // (a game not known before is recognised again once a scheme is chosen for it)
            if (found != known.end() && found->second.Stamp == FolderTime(path) && (found->second.Known || chosen.empty()))
            {
                games.push_back(found->second);
                games.back().Internal = internal;
                known.erase(found);
            }
            else
            {
                games.push_back(Identify(path, internal, chosen));
                changed = true;
            }
            if (record != played.end())
            {
                games.back().LastPlayed = record->second.LastPlayed;
                games.back().PlaySeconds = record->second.PlaySeconds;
                games.back().Scheme = chosen;
            }
        }
    // (folders gone: what is left of the cache)
    if (changed || !known.empty())
        WriteCache(cache, games);
    std::stable_sort(games.begin(), games.end(), [](const GameEntry& a, const GameEntry& b) {
        if (a.Known != b.Known)
            return a.Known;
        if (a.LastPlayed != b.LastPlayed)
            return a.LastPlayed > b.LastPlayed;
        return Lower(a.Name) < Lower(b.Name);
    });
    return games;
}

std::shared_ptr<const archives::EncryptionScheme> SchemeFor(const std::string& path, const std::string& records)
{
    auto archive = game::GameData::FindArchive(path);
    if (!archive)
        return nullptr;
    auto* formats = archives::FormatManager::Instance();
    if (auto scheme = formats->SchemeForArchive(*archive))
        return scheme;
    auto played = ReadRecords(records);
    auto record = played.find(Files::FileName(path));
    return record != played.end() && !record->second.Scheme.empty() ? formats->GetScheme(record->second.Scheme) : nullptr;
}

std::vector<SchemeCandidate> DetectSchemes(const std::string& path)
{
    // Files a scheme is tried on: per archive, the first few with a known start, in the smallest
    // archives first (their indexes are the quickest to decode) until FilesInAll were tried
    constexpr int FilesPerArchive = 3, FilesInAll = 6;
    std::vector<std::pair<int64_t, std::string>> archives;
    for (const auto& name : Files::FileNames(path))
    {
        struct stat info;
        std::string file = Files::Combine(path, name);
        if (Lower(name).ends_with(".war") && stat(file.c_str(), &info) == 0)
            archives.emplace_back(int64_t(info.st_size), file);
    }
    std::sort(archives.begin(), archives.end());
    std::vector<SchemeCandidate> results;
    for (const auto& scheme : archives::FormatManager::Instance()->KnownSchemes())
    {
        int right = 0, tried = 0;
        for (const auto& [size, file] : archives)
        {
            if (tried >= FilesInAll)
                break;
            std::unique_ptr<archives::WarcArchive> archive;
            try
            {
                archive = archives::WarcOpener::TryOpen(std::make_unique<ArcView>(file), scheme);
            }
            catch (const std::exception&)
            {
                // broken with this scheme's keys
            }
            if (!archive)
                continue;
            int here = 0;
            for (const auto& entry : archive->Entries)
            {
                std::string start = KnownStart(entry.Name);
                if (start.empty())
                    continue;
                tried++;
                try
                {
                    Bytes bytes = archives::WarcOpener::OpenEntry(*archive, entry);
                    if (bytes.size() >= start.size() && std::equal(start.begin(), start.end(), bytes.begin()))
                        right++;
                }
                catch (const std::exception&)
                {
                    // a wrong key: the file does not unpack
                }
                if (++here >= FilesPerArchive)
                    break;
            }
            // a file that came out wrong: no need to go on
            if (right < tried)
                break;
        }
        if (tried > 0 && right == tried)
            results.push_back({scheme->Name, right, tried});
    }
    std::stable_sort(results.begin(), results.end(), [](const SchemeCandidate& a, const SchemeCandidate& b) {
        return a.Tried != b.Tried ? a.Tried > b.Tried : Lower(a.Name) < Lower(b.Name);
    });
    return results;
}

} // namespace openshiina::player
