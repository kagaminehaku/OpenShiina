#include "scripting/X86Decoder.h"

#include <algorithm>

namespace openshiina::scripting {

namespace {

// The two-byte map (0F xx): M = ModRM, I = ModRM and an imm8, N = nothing more, J = Jcc rel32,
// 3 = the 0F38 map, A = the 0F3A map, X = no instruction
constexpr char kTwoByte[257] =
    //0123456789ABCDEF
    "MMMMXNNNNNXNXMNI"  // 0x: 0F 0B is UD2 (N), 0F 0F 3DNow! (ModRM and a suffix byte)
    "MMMMMMMMMMMMMMMM"  // 1x
    "MMMMXXXXMMMMMMMM"  // 2x
    "NNNNNNXN3XAXXXXX"  // 3x
    "MMMMMMMMMMMMMMMM"  // 4x
    "MMMMMMMMMMMMMMMM"  // 5x
    "MMMMMMMMMMMMMMMM"  // 6x
    "IIIIMMMNMMXXMMMM"  // 7x
    "JJJJJJJJJJJJJJJJ"  // 8x
    "MMMMMMMMMMMMMMMM"  // 9x
    "NNNMIMXXNNNMIMMM"  // Ax
    "MMMMMMMMMMIMMMMM"  // Bx
    "MMIMIIIMNNNNNNNN"  // Cx
    "MMMMMMMMMMMMMMMM"  // Dx
    "MMMMMMMMMMMMMMMM"  // Ex
    "MMMMMMMMMMMMMMMM"; // Fx

struct Cursor
{
    const uint8_t* code;
    size_t length;
    size_t p = 0;

    bool Has(size_t n) const { return p + n <= length; }
    uint8_t Next() { return code[p++]; }
};

// ModRM and what it takes (SIB, displacement); false when the bytes run out
bool ModRM(Cursor& c, bool address16, int& mod, int& reg, int& rm)
{
    if (!c.Has(1))
        return false;
    uint8_t m = c.Next();
    mod = m >> 6;
    reg = (m >> 3) & 7;
    rm = m & 7;
    if (mod == 3)
        return true;
    size_t displacement = 0;
    if (address16)
        displacement = mod == 0 ? (rm == 6 ? 2 : 0) : mod == 1 ? 1 : 2;
    else
    {
        if (rm == 4)
        {
            if (!c.Has(1))
                return false;
            uint8_t sib = c.Next();
            if (mod == 0 && (sib & 7) == 5)
                displacement = 4;
        }
        if (mod == 0 && rm == 5)
            displacement = 4;
        else if (mod == 1)
            displacement = 1;
        else if (mod == 2)
            displacement = 4;
    }
    if (!c.Has(displacement))
        return false;
    c.p += displacement;
    return true;
}

} // namespace

X86Decoded DecodeX86(const uint8_t* code, size_t available, uint32_t ip)
{
    X86Decoded d;
    Cursor c{code, std::min<size_t>(available, 15)};
    bool opsize = false, address16 = false, lock = false;
    // Prefixes
    while (c.Has(1))
    {
        uint8_t b = c.code[c.p];
        if (b == 0x66)
            opsize = true;
        else if (b == 0x67)
            address16 = true;
        else if (b == 0xF0)
            lock = true;
        else if (b != 0xF2 && b != 0xF3 && b != 0x26 && b != 0x2E && b != 0x36 && b != 0x3E && b != 0x64 && b != 0x65)
            break;
        c.p++;
    }
    if (!c.Has(1))
        return d;
    (void)lock;
    uint8_t op = c.Next();
    int mod = 0, reg = 0, rm = 0;
    size_t iz = opsize ? 2 : 4;
    auto modrm = [&] { return ModRM(c, address16, mod, reg, rm); };
    auto imm = [&](size_t n) {
        if (!c.Has(n))
            return false;
        c.p += n;
        return true;
    };
    auto done = [&](X86Flow flow = X86Flow::Next) {
        d.Invalid = false;
        d.Length = int(c.p);
        d.Flow = flow;
        return d;
    };
    auto relative = [&](size_t size, X86Flow flow) {
        if (!c.Has(size))
            return d;
        int32_t rel;
        if (size == 1)
            rel = int8_t(c.code[c.p]);
        else if (size == 2)
            rel = int16_t(uint16_t(c.code[c.p] | c.code[c.p + 1] << 8));
        else
            rel = int32_t(uint32_t(c.code[c.p]) | uint32_t(c.code[c.p + 1]) << 8 | uint32_t(c.code[c.p + 2]) << 16 | uint32_t(c.code[c.p + 3]) << 24);
        c.p += size;
        done(flow);
        uint32_t target = ip + uint32_t(c.p) + uint32_t(rel);
        // 66 makes it a 16-bit branch, which is not a NearBranch32
        d.NearBranch = !opsize;
        d.Target = opsize ? (target & 0xFFFF) : target;
        return d;
    };

    if (op == 0x0F)
    {
        if (!c.Has(1))
            return d;
        uint8_t op2 = c.Next();
        char kind = kTwoByte[op2];
        switch (kind)
        {
        case 'X':
            return d;
        case 'N':
            if (op2 == 0x0B)
                return done(X86Flow::Exception);  // UD2
            return done();
        case 'J':
            return relative(opsize ? 2 : 4, X86Flow::ConditionalBranch);
        case '3':
            if (!c.Has(1))
                return d;
            c.p++;
            return modrm() ? done() : d;
        case 'A':
            if (!c.Has(1))
                return d;
            c.p++;
            return modrm() && imm(1) ? done() : d;
        case 'I':
            if (!modrm() || !imm(1))
                return d;
            return done();
        default:
            if (!modrm())
                return d;
            if (op2 == 0x0F && !imm(1))  // 3DNow!'s suffix
                return d;
            // Memory only / register only, as Iced checks them
            if (mod == 3 && (op2 == 0xB2 || op2 == 0xB4 || op2 == 0xB5 || op2 == 0x2B || op2 == 0x13 || op2 == 0x17 || op2 == 0xE7
                             || op2 == 0xC3 || (op2 == 0xC7 && reg == 1)))
                return d;
            if (mod != 3 && (op2 == 0xD7 || op2 == 0xF7 || op2 == 0x50))
                return d;
            if (op2 == 0xB9 || op2 == 0xFF)
                return done(X86Flow::Exception);  // UD1, UD0
            return done();
        }
    }

    switch (op)
    {
    // ALU r/m, r and r, r/m; AL / eAX, imm
    case 0x00: case 0x01: case 0x02: case 0x03: case 0x08: case 0x09: case 0x0A: case 0x0B:
    case 0x10: case 0x11: case 0x12: case 0x13: case 0x18: case 0x19: case 0x1A: case 0x1B:
    case 0x20: case 0x21: case 0x22: case 0x23: case 0x28: case 0x29: case 0x2A: case 0x2B:
    case 0x30: case 0x31: case 0x32: case 0x33: case 0x38: case 0x39: case 0x3A: case 0x3B:
    case 0x84: case 0x85: case 0x86: case 0x87: case 0x88: case 0x89: case 0x8A: case 0x8B:
    case 0x8C: case 0x8E: case 0x63:
    case 0xD0: case 0xD1: case 0xD2: case 0xD3:
    case 0xD8: case 0xD9: case 0xDA: case 0xDB: case 0xDC: case 0xDD: case 0xDE: case 0xDF:
        return modrm() ? done() : d;
    case 0x04: case 0x0C: case 0x14: case 0x1C: case 0x24: case 0x2C: case 0x34: case 0x3C:
    case 0xA8: case 0x6A: case 0xD4: case 0xD5: case 0xE4: case 0xE5: case 0xE6: case 0xE7:
        return imm(1) ? done() : d;
    case 0x05: case 0x0D: case 0x15: case 0x1D: case 0x25: case 0x2D: case 0x35: case 0x3D:
    case 0xA9: case 0x68:
        return imm(iz) ? done() : d;
    case 0x06: case 0x07: case 0x0E: case 0x16: case 0x17: case 0x1E: case 0x1F:
    case 0x27: case 0x2F: case 0x37: case 0x3F:
    case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
    case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D: case 0x4E: case 0x4F:
    case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
    case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
    case 0x60: case 0x61: case 0x6C: case 0x6D: case 0x6E: case 0x6F:
    case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
    case 0x98: case 0x99: case 0x9B: case 0x9C: case 0x9D: case 0x9E: case 0x9F:
    case 0xA4: case 0xA5: case 0xA6: case 0xA7: case 0xAA: case 0xAB: case 0xAC: case 0xAD: case 0xAE: case 0xAF:
    case 0xC9: case 0xD6: case 0xD7: case 0xEC: case 0xED: case 0xEE: case 0xEF:
    case 0xF4: case 0xF5: case 0xF8: case 0xF9: case 0xFA: case 0xFB: case 0xFC: case 0xFD:
        return done();
    case 0x62: // BOUND (with mod 3 an EVEX prefix: not in the games' routines)
    case 0x8D: // LEA
    case 0xC4: case 0xC5: // LES / LDS (with mod 3 a VEX prefix)
        if (!modrm() || mod == 3)
            return d;
        return done();
    case 0x69: case 0x81: case 0xC7:
        if (!modrm() || !imm(iz))
            return d;
        if (op == 0xC7 && reg != 0)
            return d;
        return done();
    case 0x6B: case 0x80: case 0x82: case 0x83: case 0xC0: case 0xC1: case 0xC6:
        if (!modrm() || !imm(1))
            return d;
        if (op == 0xC6 && reg != 0)
            return d;
        return done();
    case 0x8F:
        if (!modrm() || reg != 0)
            return d;
        return done();
    case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77:
    case 0x78: case 0x79: case 0x7A: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F:
    case 0xE0: case 0xE1: case 0xE2: case 0xE3:
        return relative(1, X86Flow::ConditionalBranch);
    case 0xEB:
        return relative(1, X86Flow::UnconditionalBranch);
    case 0xE9:
        return relative(opsize ? 2 : 4, X86Flow::UnconditionalBranch);
    case 0xE8:
        return relative(opsize ? 2 : 4, X86Flow::Call);
    case 0x9A: // CALL ptr16:32
        return imm(iz + 2) ? done(X86Flow::Call) : d;
    case 0xEA: // JMP ptr16:32
        return imm(iz + 2) ? done(X86Flow::UnconditionalBranch) : d;
    case 0xA0: case 0xA1: case 0xA2: case 0xA3:
        return imm(address16 ? 2 : 4) ? done() : d;
    case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
        return imm(1) ? done() : d;
    case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF:
        return imm(iz) ? done() : d;
    case 0xC2: case 0xCA:
        return imm(2) ? done(X86Flow::Return) : d;
    case 0xC3: case 0xCB: case 0xCF:
        return done(X86Flow::Return);
    case 0xC8:
        return imm(3) ? done() : d;
    case 0xCC: case 0xCE: case 0xF1:
        return done(X86Flow::Interrupt);
    case 0xCD:
        return imm(1) ? done(X86Flow::Interrupt) : d;
    case 0xF6:
        if (!modrm())
            return d;
        if (reg <= 1 && !imm(1))
            return d;
        return done();
    case 0xF7:
        if (!modrm())
            return d;
        if (reg <= 1 && !imm(iz))
            return d;
        return done();
    case 0xFE:
        if (!modrm() || reg > 1)
            return d;
        return done();
    case 0xFF:
        if (!modrm() || reg == 7 || (mod == 3 && (reg == 3 || reg == 5)))
            return d;
        if (reg == 2 || reg == 3)
            return done(X86Flow::IndirectCall);
        if (reg == 4 || reg == 5)
            return done(X86Flow::IndirectBranch);
        return done();
    default:
        return d;
    }
}

} // namespace openshiina::scripting
