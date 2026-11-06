// Grand†CROSS / ShiinaRio archive encryption scheme definitions (C# Archives/EncryptionScheme.cs)
// Ported from GARbro ArcFormats/ShiinaRio/WarcEncryption.cs
#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "io/Binary.h"

namespace openshiina::archives {

/// The extra step of a game's entries before (flags 0x202) and after (0x204) unpacking.
class IDecryptExtra
{
public:
    virtual ~IDecryptExtra() = default;
    /// The length bytes of data from index; a byte outside data is an InvalidDataError (an index
    /// error in C#).
    virtual void Decrypt(Bytes& data, size_t index, uint32_t length, uint32_t flags) = 0;
};

/// ShiinaImage as in GARbro: the first commonLength bytes of an array shared by many games (the
/// engine's built-in JPEG), followed by a per-game tail (C# IO/ByteArray.cs ImageArray).
class ImageArray
{
public:
    ImageArray(std::shared_ptr<const Bytes> common, size_t commonLength, Bytes tail);
    size_t Length() const { return m_commonLength + m_tail.size(); }
    uint8_t operator[](size_t i) const { return i < m_commonLength ? (*m_common)[i] : m_tail[i - m_commonLength]; }

private:
    std::shared_ptr<const Bytes> m_common;
    size_t m_commonLength;
    Bytes m_tail;
};

struct EncryptionScheme
{
    std::string Name;
    std::string OriginalTitle;
    int Version = 0;
    int EntryNameSize = 32;
    std::optional<Bytes> CryptKey;
    std::optional<std::vector<uint32_t>> HelperKey;
    std::optional<Bytes> Region;
    std::optional<Bytes> DecodeBin;
    std::shared_ptr<const ImageArray> ShiinaImage;
    std::shared_ptr<IDecryptExtra> ExtraCrypt;
    /// The game's .exe is large address aware ("LargeAddressAware": true): for the scripts' heap
    /// when the game folder has no .exe to read it from.
    bool LargeAddressAware = false;

    /// WARC 1.0 and 1.1 archives: 16-byte names and no keys (GARbro's EncryptionScheme.Warc110),
    /// named as the game it opens is (its folder, for a game no GameMap entry tells).
    static std::shared_ptr<EncryptionScheme> Warc110(const std::string& name);
};

/// The ExtraCrypt of Formats.Json: its "$type" (the last part of the C# class name) with its
/// "Seed", "DecodeTable" or "Key"; throws InvalidDataError for a type there is no class of.
struct ExtraCryptParameters
{
    std::string Type;
    uint32_t Seed = 0;
    Bytes Table;
    Bytes KeyBytes;
    uint32_t Key = 0;
};
std::shared_ptr<IDecryptExtra> CreateExtraCrypt(const ExtraCryptParameters& p);

} // namespace openshiina::archives
