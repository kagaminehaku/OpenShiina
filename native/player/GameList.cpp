// The Switch player's list of games (see GameList.h)
#include "GameList.h"

#include <sys/stat.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <map>

#include "archives/FormatManager.h"
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

// C# GameLibrary.Identify, without the schemes chosen by hand (the Switch player has no list of
// them to choose from): the scheme the archives and the .exe tell, its name, its .exe
GameEntry Identify(const std::string& path, bool internal)
{
    GameEntry game{path, Files::FileName(path), {}, false, internal, FolderTime(path)};
    auto archive = game::GameData::FindArchive(path);
    if (!archive)
        return game;
    auto* formats = archives::FormatManager::Instance();
    auto scheme = formats->SchemeForArchive(*archive);
    if (!scheme)
        return game;
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

// library.tsv: a game a line, its folder, time, known (1 / 0), name and .exe, by tabs
std::map<std::string, GameEntry> ReadCache(const std::string& cache)
{
    std::map<std::string, GameEntry> games;
    Bytes data;
    if (!Files::ReadAll(cache, data))
        return games;
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
        if (fields.size() == 5)
        {
            GameEntry game{fields[0], fields[3], fields[4], fields[2] == "1", false, std::strtoll(fields[1].c_str(), nullptr, 10)};
            games[game.Path] = game;
        }
        start = end + 1;
    }
    return games;
}

void WriteCache(const std::string& cache, const std::vector<GameEntry>& games)
{
    std::string text;
    for (const auto& g : games)
        text += g.Path + '\t' + std::to_string(g.Stamp) + '\t' + (g.Known ? "1" : "0") + '\t' + g.Name + '\t' + g.Exe + '\n';
    if (std::FILE* f = Files::Open(cache, "wb"))
    {
        std::fwrite(text.data(), 1, text.size(), f);
        std::fclose(f);
    }
}

} // namespace

std::vector<GameEntry> ListGames(const std::vector<std::pair<std::string, bool>>& roots, const std::string& cache, bool again)
{
    auto known = again ? std::map<std::string, GameEntry>() : ReadCache(cache);
    std::vector<GameEntry> games;
    bool changed = again;
    for (const auto& [root, internal] : roots)
        for (const auto& folder : Files::FolderNames(root))
        {
            std::string path = Files::Combine(root, folder);
            auto found = known.find(path);
            if (found != known.end() && found->second.Stamp == FolderTime(path))
            {
                games.push_back(found->second);
                games.back().Internal = internal;
                known.erase(found);
                continue;
            }
            games.push_back(Identify(path, internal));
            changed = true;
        }
    // (folders gone: what is left of the cache)
    if (changed || !known.empty())
        WriteCache(cache, games);
    std::stable_sort(games.begin(), games.end(), [](const GameEntry& a, const GameEntry& b) {
        if (a.Known != b.Known)
            return a.Known;
        std::string x = a.Name, y = b.Name;
        for (auto* s : {&x, &y})
            std::transform(s->begin(), s->end(), s->begin(), [](unsigned char c) { return char(c >= 'A' && c <= 'Z' ? c + 32 : c); });
        return x < y;
    });
    return games;
}

} // namespace openshiina::player
