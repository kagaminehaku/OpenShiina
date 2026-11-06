// Operand layouts of the SCN opcodes (C# Scripting/ScnOpcodes.cs), from the opcode tables read out
// of the engine executables (Data/ScnOps). Opcode numbers are the same in every engine version;
// later versions only add opcodes.
#pragma once

#include <array>
#include <memory>
#include <string>
#include <vector>

namespace openshiina::scripting {

/// One item of an opcode's operands.
struct LayoutItem
{
    enum Kind : uint8_t
    {
        Operand,    // "V": a typed operand
        RawBytes,   // "b1" / "b2" / "b4" ...: raw bytes, Size of them
        Counted,    // "N2V": a u16 count, then that many operands
        Switch,     // "SW": a switch table
        Case,       // "CASE": a case entry
        UpToFF,     // "VARGS": operands up to an FF byte
        UpToMinus1, // "M1V": operands up to a constant -1
        Sprite234,  // "SPRITE234": 04BD of v2.34
        Glyph250,   // "GLYPH250": 009C of v2.50
    };
    Kind kind;
    int size = 0;
};

class ScnOpcodes
{
public:
    /// The opcode's layout, or null when the table has no such opcode.
    const std::vector<LayoutItem>* Layout(int op) const
    {
        return op >= 0 && op < 0x10000 && m_known[size_t(op)] ? &m_layouts[size_t(op)] : nullptr;
    }

    bool HasLayout(int op) const { return Layout(op) != nullptr; }

    /// The table for an engine version as RIO.INI names it ("2.47"), read from `folder` (the
    /// tables' .tsv files) the first time; throws std::runtime_error when it is missing.
    static const ScnOpcodes& ForVersion(const std::string& folder, const std::string& version);

    /// The table an engine version uses ("ops_v247" for "2.45").
    static std::string TableName(const std::string& version);

private:
    ScnOpcodes(const std::string& table, const std::string& name);

    std::vector<std::vector<LayoutItem>> m_layouts = std::vector<std::vector<LayoutItem>>(0x10000);
    std::vector<bool> m_known = std::vector<bool>(0x10000);
};

} // namespace openshiina::scripting
