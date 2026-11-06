// Grand†CROSS / ShiinaRio archive Decoder implementation (C# Archives/WarcEncryption.cs)
// Ported from GARbro ArcFormats/ShiinaRio/WarcEncryption.cs
//
// The keys are worked out with doubles (acos, log, exp, pow, sin, cos): the build turns off fused
// multiply-adds (-ffp-contract=off) so that each operation rounds as in the C# player, and
// tools/arcdump compares every file's bytes with the C# player's on each platform, since the C
// libraries' functions may round their last bit differently.
#pragma once

#include <cstdint>
#include <memory>

#include "archives/EncryptionScheme.h"

namespace openshiina::archives {

class Decoder
{
public:
    Decoder(int version, std::shared_ptr<const EncryptionScheme> scheme);

    int SchemeVersion() const { return m_scheme->Version; }
    int WarcVersion() const { return m_warcVersion; }
    uint32_t MaxIndexLength() const { return m_maxIndexLength; }
    int EntryNameSize() const { return m_scheme->EntryNameSize; }
    IDecryptExtra* ExtraCrypt() const { return m_scheme->ExtraCrypt.get(); }

    void Decrypt(Bytes& data, size_t index, uint32_t dataLength);
    void DecryptIndex(uint32_t indexOffset, Bytes& index);
    void Decrypt2(Bytes& data, size_t index, uint32_t length);

private:
    void DecryptContent(int x, Bytes& data, size_t index, uint32_t length);
    void XorIndex(uint32_t indexOffset, Bytes& index);
    double DecryptHelper1(double a);
    uint32_t DecryptHelper2(double a);
    static uint32_t DecryptHelper3(uint32_t key);
    void DecryptHelper4(Bytes& data, size_t index, const std::vector<uint32_t>& keySrc);
    uint32_t RegionCrc32(const Bytes& src, uint32_t flags, uint32_t rgb);
    uint32_t NextRand();
    uint32_t GetMaxIndexLength(int version) const;

    std::shared_ptr<const EncryptionScheme> m_scheme;
    int m_warcVersion;
    uint32_t m_maxIndexLength;
    uint32_t m_rand = 0;
};

} // namespace openshiina::archives
