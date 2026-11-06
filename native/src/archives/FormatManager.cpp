#include "archives/FormatManager.h"

#include <json.hpp>

#include "archives/WarcOpener.h"
#include "platform/Files.h"
#include "text/Text.h"
#include "util/Encoding.h"

namespace openshiina::archives {

using Json = nlohmann::ordered_json;

template <typename T>
void CaseInsensitiveMap<T>::Set(const std::string& key, T value)
{
    std::string folded = Text::FoldCase(key);
    auto it = m_index.find(folded);
    if (it != m_index.end())
    {
        m_items[it->second].second = std::move(value);
        return;
    }
    m_index.emplace(std::move(folded), m_items.size());
    m_items.emplace_back(key, std::move(value));
}

template <typename T>
const T* CaseInsensitiveMap<T>::Find(const std::string& key) const
{
    auto it = m_index.find(Text::FoldCase(key));
    return it == m_index.end() ? nullptr : &m_items[it->second].second;
}

template class CaseInsensitiveMap<std::string>;
template class CaseInsensitiveMap<std::shared_ptr<EncryptionScheme>>;

namespace {

std::unique_ptr<FormatManager> s_instance;

std::string GetString(const Json& e, const char* name)
{
    auto it = e.find(name);
    return it != e.end() && it->is_string() ? it->get<std::string>() : std::string();
}

int GetInt32(const Json& e, const char* name)
{
    auto it = e.find(name);
    if (it == e.end() || !it->is_number_integer())
        throw InvalidDataError(std::string("missing '") + name + "'");
    return it->get<int>();
}

uint32_t GetUInt32(const Json& e, const char* name)
{
    auto it = e.find(name);
    if (it == e.end() || !it->is_number_integer() || it->get<int64_t>() < 0 || it->get<int64_t>() > 0xFFFFFFFFll)
        throw InvalidDataError(std::string("missing '") + name + "'");
    return uint32_t(it->get<int64_t>());
}

/// What the schemes share: hex byte arrays by name, and the common parts of their ShiinaImage.
struct Shared
{
    std::unordered_map<std::string, std::string> Hex;
    std::unordered_map<std::string, std::shared_ptr<const Bytes>> CommonImages;
};

/// A byte array as hex, or "@name" of one in SharedData.
std::optional<Bytes> GetHex(const Json& e, const char* name, const Shared& shared)
{
    auto it = e.find(name);
    if (it == e.end() || !it->is_string())
        return std::nullopt;
    const std::string& text = it->get_ref<const std::string&>();
    if (!text.empty() && text[0] == '@')
    {
        auto found = shared.Hex.find(text.substr(1));
        if (found == shared.Hex.end())
            throw InvalidDataError(std::string(name) + " names '" + text + "', which SharedData does not have");
        return Encoding::FromHex(found->second);
    }
    return Encoding::FromHex(text);
}

std::optional<std::vector<uint32_t>> GetHexUInts(const Json& e, const char* name)
{
    auto it = e.find(name);
    if (it == e.end() || !it->is_array())
        return std::nullopt;
    std::vector<uint32_t> values;
    for (const auto& t : *it)
    {
        std::string s = t.get<std::string>();
        size_t first = s.find_first_not_of(" \t"), last = s.find_last_not_of(" \t");
        s = first == std::string::npos ? std::string() : s.substr(first, last - first + 1);
        if (s.size() >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
            s = s.substr(2);
        if (s.empty() || s.size() > 8 || s.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos)
            throw InvalidDataError(std::string(name) + " has a value that is not a hex number");
        values.push_back(uint32_t(std::stoul(s, nullptr, 16)));
    }
    return values;
}

/// ShiinaImage = the first "CommonLength" bytes of a common part (CommonImages) + the scheme's own
/// "Tail" (GARbro's ImageArray); "Length" and "Sha256" describe the image put together.
std::shared_ptr<const ImageArray> LoadShiinaImage(const Json& scheme, const Shared& shared)
{
    auto it = scheme.find("ShiinaImage");
    if (it == scheme.end() || it->is_null())
        return nullptr;
    const Json& img = *it;
    std::string commonName = GetString(img, "Common");
    auto common = shared.CommonImages.find(commonName);
    if (common == shared.CommonImages.end())
        throw InvalidDataError("ShiinaImage names the common part '" + commonName + "', which CommonImages does not have");
    size_t commonLength = img.contains("CommonLength") ? size_t(GetInt32(img, "CommonLength")) : common->second->size();
    if (commonLength > common->second->size())
        throw InvalidDataError("the common part '" + commonName + "' is shorter than CommonLength");
    Bytes tail = img.contains("Tail") ? Encoding::FromBase64(GetString(img, "Tail")) : Bytes();

    Encoding::Sha256 hash;
    hash.Append(common->second->data(), commonLength);
    hash.Append(tail.data(), tail.size());
    auto image = std::make_shared<ImageArray>(common->second, commonLength, std::move(tail));
    if (img.contains("Length") && size_t(GetInt32(img, "Length")) != image->Length())
        throw InvalidDataError("ShiinaImage is not of the length Formats.Json gives");
    if (img.contains("Sha256"))
    {
        auto digest = hash.Finish();
        if (!Text::EqualsIgnoreCase(Encoding::ToHex(digest.data(), digest.size()), GetString(img, "Sha256")))
            throw InvalidDataError("ShiinaImage SHA-256 does not match Formats.Json");
    }
    return image;
}

std::shared_ptr<IDecryptExtra> LoadExtraCrypt(const Json& scheme, const Shared& shared)
{
    auto it = scheme.find("ExtraCrypt");
    if (it == scheme.end() || it->is_null())
        return nullptr;
    const Json& x = *it;
    ExtraCryptParameters p;
    std::string fullType = GetString(x, "$type");
    p.Type = fullType.substr(fullType.find_last_of('.') + 1);
    // The fields the type has: CreateExtraCrypt takes those it needs
    if (x.contains("Seed")) p.Seed = GetUInt32(x, "Seed");
    if (auto table = GetHex(x, "DecodeTable", shared)) p.Table = std::move(*table);
    auto key = x.find("Key");
    if (key != x.end() && key->is_string())
        p.KeyBytes = *GetHex(x, "Key", shared);
    else if (key != x.end())
        p.Key = GetUInt32(x, "Key");
    // A type that needs a field it lacks: as the C# player's GetProperty, a missing field
    if ((p.Type == "YuruPlusCrypt" || p.Type == "MakiFesCrypt" || p.Type == "KeyAdlerCrypt"
         || p.Type == "ShojoMamaCrypt" || p.Type == "TestamentCrypt") && !x.contains("Seed"))
        throw InvalidDataError("missing 'Seed'");
    if (p.Type == "UshimitsuCrypt" && key == x.end())
        throw InvalidDataError("missing 'Key'");
    return CreateExtraCrypt(p);
}

std::shared_ptr<EncryptionScheme> ParseScheme(const Json& e, const Shared& shared)
{
    auto s = std::make_shared<EncryptionScheme>();
    s->Name = GetString(e, "Name");
    s->OriginalTitle = GetString(e, "OriginalTitle");
    s->Version = GetInt32(e, "Version");
    s->EntryNameSize = GetInt32(e, "EntryNameSize");
    s->CryptKey = GetHex(e, "CryptKey", shared);
    s->HelperKey = GetHexUInts(e, "HelperKey");
    s->Region = GetHex(e, "Region", shared);
    s->DecodeBin = GetHex(e, "DecodeBin", shared);
    s->ShiinaImage = LoadShiinaImage(e, shared);
    s->ExtraCrypt = LoadExtraCrypt(e, shared);
    auto laa = e.find("LargeAddressAware");
    s->LargeAddressAware = laa != e.end() && laa->is_boolean() && laa->get<bool>();
    return s;
}

} // namespace

FormatManager* FormatManager::Instance() { return s_instance.get(); }
void FormatManager::SetInstance(std::unique_ptr<FormatManager> manager) { s_instance = std::move(manager); }

FormatManager::FormatManager(const std::string& jsonPath)
    : m_path(jsonPath)
{
    try
    {
        Load(jsonPath);
    }
    catch (const std::exception& ex)
    {
        m_loadErrors.push_back("Failed to read " + jsonPath + ": " + ex.what());
    }
}

void FormatManager::Load(const std::string& jsonPath)
{
    Bytes bytes;
    if (!Files::ReadAll(jsonPath, bytes))
        throw std::runtime_error(std::string(SchemeFileName) + " could not be read");
    Json root = Json::parse(bytes.begin(), bytes.end());

    Shared shared;
    if (auto it = root.find("SharedData"); it != root.end())
        for (const auto& [name, value] : it->items())
            shared.Hex[name] = value.is_string() ? value.get<std::string>() : std::string();
    if (auto it = root.find("CommonImages"); it != root.end())
        for (const auto& [name, value] : it->items())
            shared.CommonImages[name] = std::make_shared<const Bytes>(Encoding::FromBase64(value.is_string() ? value.get<std::string>() : std::string()));

    if (auto it = root.find("GameMap"); it != root.end())
        for (const auto& [name, value] : it->items())
            m_gameMap.Set(name, value.is_string() ? value.get<std::string>() : std::string());

    auto schemeMap = root.find("SchemeMap");
    if (schemeMap == root.end())
        throw InvalidDataError("missing 'SchemeMap'");

    for (const auto& [format, value] : schemeMap->items())
    {
        auto known = value.find("KnownSchemes");
        if (known == value.end())
            continue;
        for (const auto& item : *known)
        {
            std::string name = GetString(item, "Name");
            try
            {
                auto scheme = ParseScheme(item, shared);
                m_schemes.Set(scheme->Name, scheme);
                m_known.push_back(scheme);
            }
            catch (const std::exception& ex)
            {
                m_loadErrors.push_back("Scheme '" + name + "' skipped: " + ex.what());
            }
        }
    }
}

std::shared_ptr<EncryptionScheme> FormatManager::GetScheme(const std::string& name) const
{
    const auto* s = m_schemes.Find(name);
    return s ? *s : nullptr;
}

std::shared_ptr<EncryptionScheme> FormatManager::SchemeForArchive(const std::string& arcPath) const
{
    if (auto title = LookupGame(arcPath))
        if (auto scheme = GetScheme(*title))
            return scheme;
    if (auto version = WarcOpener::ArchiveVersion(arcPath); version && WarcOpener::NeedsNoScheme(*version))
    {
        std::string folder = Files::FileName(Files::DirectoryName(arcPath));
        return EncryptionScheme::Warc110(folder.empty() ? "ShiinaRio" : folder);
    }
    return nullptr;
}

std::optional<std::string> FormatManager::LookupGame(const std::string& arcPath) const
{
    std::string dir = Files::DirectoryName(arcPath);
    if (dir.empty())
        dir = ".";

    // Check if known game exe exists in same directory (any case: the folder may be on Linux)
    CaseInsensitiveMap<bool> files;
    for (const auto& name : Files::FileNames(dir))
        files.Set(name, true);
    for (const auto& [exe, title] : m_gameMap.Items())
        if (files.Find(exe))
            return title;

    // Check archive file name against game map or scheme names
    std::string fileName = Files::FileNameWithoutExtension(arcPath);
    for (const auto& [exe, title] : m_gameMap.Items())
        if (Text::ContainsIgnoreCase(fileName, Files::FileNameWithoutExtension(exe)))
            return title;

    for (const auto& [scheme, _] : m_schemes.Items())
        if (Text::ContainsIgnoreCase(fileName, scheme))
            return scheme;

    return std::nullopt;
}

} // namespace openshiina::archives
