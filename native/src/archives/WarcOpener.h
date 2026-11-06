// Archive opener for ShiinaRio WAR/WARC archives (C# Archives/WarcOpener.cs)
// Ported from GARbro ArcFormats/ShiinaRio/ArcWARC.cs and ArcWARC1.0.cs (morkt, MIT).
//   WARC 1.7: the index zlib-packed, keys of the game's scheme
//   WARC 1.2-1.6: the index packed with a range coder (WarcRangeDecoder), keys of the game's scheme
//   WARC 1.1: the index as it is, 16-byte names, no keys (EncryptionScheme::Warc110)
//   WARC 1.0: Forest's archives, ShiinaRio's predecessor: the index XORed with FE E5, 16-byte
//             names, entries "Ylz" packed with the 16-bit-control LZ (Ylz16Reader), XOR E6
#pragma once

#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "archives/Decoder.h"
#include "io/ArcView.h"

namespace openshiina::archives {

struct Entry
{
    /// The name as UTF-8 (the archive keeps it as code page 932).
    std::string Name;
    std::string Type;
    int64_t Offset = -1;
    uint32_t Size = 0;
    uint32_t UnpackedSize = 0;
    bool IsPacked = false;
    /// A WARC 1.1+ entry (with the file time and flags below); WARC 1.0 entries have neither.
    bool IsWarc = false;
    int64_t FileTime = 0;
    uint32_t Flags = 0;

    bool CheckPlacement(int64_t maxOffset) const
    {
        return Offset < maxOffset && Size <= maxOffset && Offset <= maxOffset - Size;
    }
};

class WarcArchive
{
public:
    WarcArchive(std::unique_ptr<ArcView> file, std::vector<Entry> entries, std::unique_ptr<Decoder> decoder, std::string schemeName)
        : File(std::move(file)), Entries(std::move(entries)), SchemeName(std::move(schemeName)), m_decoder(std::move(decoder))
    {
    }

    std::unique_ptr<ArcView> File;
    std::vector<Entry> Entries;
    std::string SchemeName;
    int WarcVersion() const { return m_decoder->WarcVersion(); }

private:
    friend class WarcOpener;
    // The file's window and the decoder's PRNG state are shared per archive: OpenEntry takes the
    // lock while it reads and decrypts
    std::unique_ptr<Decoder> m_decoder;
    std::mutex m_lock;
};

class WarcOpener
{
public:
    /// True if the file carries the ShiinaRio "WARC 1.x" signature (any version).
    static bool IsWarc(ArcView& file);

    /// The WARC version of the archive at path (100 for 1.0 ... 170 for 1.7), or nothing.
    static std::optional<int> ArchiveVersion(const std::string& path);

    /// Archives older than WARC 1.2 have no keys: any scheme opens them, none is needed.
    static bool NeedsNoScheme(int version) { return version <= 110; }

    /// The archive opened with the scheme (or the one FormatManager finds for it), or null when it
    /// is not one or the scheme does not open it; may throw on broken data, as the C# player does.
    static std::unique_ptr<WarcArchive> TryOpen(std::unique_ptr<ArcView> file, std::shared_ptr<const EncryptionScheme> selectedScheme = nullptr);

    /// The entry's file as the game reads it: decrypted and unpacked.
    static Bytes OpenEntry(WarcArchive& warc, const Entry& entry);

private:
    static std::unique_ptr<WarcArchive> TryOpenForest(std::unique_ptr<ArcView> file, const std::string& name);
    static Bytes OpenForestEntry(WarcArchive& warc, const Entry& entry);
};

} // namespace openshiina::archives
