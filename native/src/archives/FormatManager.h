// FormatManager: loads the per-game encryption schemes from Formats.Json and maps game executables
// to schemes for auto-detection (C# Archives/FormatManager.cs, where the file is described).
#pragma once

#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

#include "archives/EncryptionScheme.h"

namespace openshiina::archives {

/// A map with keys compared without regard to case that keeps the order its keys first came in,
/// as the C# player's Dictionary with StringComparer.OrdinalIgnoreCase enumerates them.
template <typename T>
class CaseInsensitiveMap
{
public:
    void Set(const std::string& key, T value);
    const T* Find(const std::string& key) const;
    /// The keys and values in the order the keys were first set.
    const std::vector<std::pair<std::string, T>>& Items() const { return m_items; }

private:
    std::vector<std::pair<std::string, T>> m_items;
    std::unordered_map<std::string, size_t> m_index;
};

class FormatManager
{
public:
    static constexpr const char* SchemeFileName = "Formats.Json";

    /// Loads the schemes of a Formats.Json; the problems found are in LoadErrors (none loaded when
    /// the file cannot be read).
    explicit FormatManager(const std::string& jsonPath);

    /// The one the archives look schemes up in when none is given; null until one is set.
    static FormatManager* Instance();
    static void SetInstance(std::unique_ptr<FormatManager> manager);

    const std::string& SchemeFilePath() const { return m_path; }
    const std::vector<std::string>& LoadErrors() const { return m_loadErrors; }
    const CaseInsensitiveMap<std::string>& GameMap() const { return m_gameMap; }
    const std::vector<std::shared_ptr<EncryptionScheme>>& KnownSchemes() const { return m_known; }

    std::shared_ptr<EncryptionScheme> GetScheme(const std::string& name) const;

    /// The scheme for the game the archive at arcPath belongs to: the one its folder's .exe (or the
    /// archive's name) tells; for WARC 1.0 / 1.1 archives, which have no keys, one without keys named
    /// after the folder when nothing tells. Null when none fits.
    std::shared_ptr<EncryptionScheme> SchemeForArchive(const std::string& arcPath) const;

    /// The title the archive's folder (a known .exe in it) or its name tells, or nothing.
    std::optional<std::string> LookupGame(const std::string& arcPath) const;

private:
    void Load(const std::string& jsonPath);

    std::string m_path;
    std::vector<std::string> m_loadErrors;
    CaseInsensitiveMap<std::string> m_gameMap;
    CaseInsensitiveMap<std::shared_ptr<EncryptionScheme>> m_schemes;
    std::vector<std::shared_ptr<EncryptionScheme>> m_known;
};

} // namespace openshiina::archives
