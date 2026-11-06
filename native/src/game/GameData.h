// An installed game (C# Game/GameData.cs): every .WAR archive of the game folder, with files looked
// up the way the scripts name them ("e\ev01_00.S25", "v\KIR0001.ogv", ...). The directory prefix
// only tells the engine which archive to search, and the extension may differ from the stored one
// (scripts say "bgm16.ogg" for BGM16.OGV), so files are found by their stem. Loose files of the
// game folder (RIO.INI, movies) are found by their path. Names are matched without regard to case
// and with either slash, as on Windows. Paths and names here are UTF-8.
#pragma once

#include <initializer_list>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <unordered_map>
#include <vector>

#include "archives/WarcOpener.h"
#include "io/Binary.h"

namespace openshiina::game {

class GameData
{
public:
    /// Opens every archive of the game folder with the scheme; throws InvalidDataError when none opens.
    static std::unique_ptr<GameData> Open(const std::string& folder, std::shared_ptr<const archives::EncryptionScheme> scheme);

    /// The first .WAR archive of a game folder (by name, any case), or none.
    static std::optional<std::string> FindArchive(const std::string& folder);

    /// The file a Windows-style relative path names under the folder, matching each part without
    /// regard to case; none when there is none or the path leaves the folder.
    static std::optional<std::string> ResolvePath(const std::string& folder, const std::string& path);

    /// The engine's settings in the folder: RIO.INI, or for older builds the .INI whose first
    /// section is the engine's ("[椎名里緒 v2.34]"), one named as an .exe first.
    static std::optional<std::string> EngineIni(const std::string& folder);

    const std::string& Folder() const { return m_folder; }
    const std::string& SchemeName() const { return m_schemeName; }

    /// The scripts mount (00DD) or unmount (00DF) an archive, named by its file. True when a file
    /// that is in more than one archive may now come from another.
    bool Mount(const std::string& archive, bool mounted);

    /// A file of the archives by a script path; false when there is none.
    bool Read(const std::string& path, Bytes& data, std::initializer_list<const char*> extensions = {});

    /// The size Read gives for a script path, from the archive's index, or none.
    std::optional<int64_t> Size(const std::string& path, std::initializer_list<const char*> extensions = {});

    /// The size of a file of one archive, named by its file ("BITCHES3_PA.WAR"), or none.
    std::optional<int64_t> Size(const std::string& archive, const std::string& path);

    /// The copy of the game's executable a game keeps in its archives under another name, found by
    /// what it is (MZ, then PE); false when there is none. Looked for once.
    bool ExecutableCopy(std::optional<int64_t> exeSize, Bytes& data);

    /// Full path of a loose file of the game folder, or none.
    std::optional<std::string> LooseFile(const std::string& path);

private:
    struct Candidate
    {
        archives::WarcArchive* Archive;
        const archives::Entry* Entry;
    };

    GameData(std::string folder, std::string schemeName) : m_folder(std::move(folder)), m_schemeName(std::move(schemeName)) {}
    std::optional<Candidate> Find(const std::string& path, std::initializer_list<const char*> extensions);
    template <typename F> std::optional<Candidate> Pick(const std::vector<Candidate>& list, F match);
    std::optional<std::string> ResolveListed(const std::string& path);

    std::string m_folder, m_schemeName;
    std::vector<std::unique_ptr<archives::WarcArchive>> m_archives;
    // Entries by their stem (folded), the stems in the order first met
    std::unordered_map<std::string, std::vector<Candidate>> m_byStem;
    std::vector<std::string> m_stems;
    std::vector<archives::WarcArchive*> m_mounted;
    std::set<archives::WarcArchive*> m_shared;
    bool m_executableSearched = false, m_executableFound = false;
    Bytes m_executableCopy;
    // The folders as first listed: their files and folders by folded name
    struct Listing
    {
        std::map<std::string, std::string> Files, Folders;
    };
    std::map<std::string, std::optional<Listing>> m_listings;
};

} // namespace openshiina::game
