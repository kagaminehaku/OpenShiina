// The variable-length code tables of the VC-1 decoder (Vc1Video), made once (C#: its static fields).
#pragma once

#include <array>

#include "formats/VlcTable.h"

namespace openshiina::formats {

/// The variable-length code tables, made once.
struct Vc1Vlcs
{
    VlcTable Imode, Norm2, Norm6;
    std::array<VlcTable, 3> Ttmb, Ttblk, Subblkpat;
    std::array<VlcTable, 4> CbpcyP, MvDiff;
    std::array<VlcTable, 8> Ac;
    VlcTable MbIntra;
    std::array<std::array<VlcTable, 2>, 2> Dc;

    Vc1Vlcs();
};

const Vc1Vlcs& Vlcs();

} // namespace openshiina::formats
