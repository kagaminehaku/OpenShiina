// C# Scripting/ScnVm.cs
#include "scripting/ScnVm.h"

#include <algorithm>
#include <format>

#include "archives/FormatManager.h"
#include "text/Text.h"

namespace openshiina::scripting {

ScreenArea ScreenArea::Union(const ScreenArea& other) const
{
    if (IsEmpty())
        return other;
    if (other.IsEmpty())
        return *this;
    return {std::min(L, other.L), std::min(T, other.T), std::max(R, other.R), std::max(B, other.B)};
}

ScreenArea ScreenArea::Clip(int width, int height) const
{
    return {std::max(L, 0), std::max(T, 0), std::min(R, width), std::min(B, height)};
}

ScnVm::ScnVm(const ScnOpcodes& opcodes, IScnHost& host)
    : m_memory(std::make_unique<ScnAddressSpace>()), m_opcodes(opcodes), m_host(host)
{
    // An engine without op_AA82 (before v2.44) has no such switch: its file writes always happen
    FileWritesEnabled = !opcodes.HasLayout(0xAA82);
    for (int i = 0; i < Slots; i++)
        m_slots[size_t(i)].Slot = i;
    // In the C# player's order: a later part replaces an earlier one's handler
    RegisterCore();
    RegisterNativeCall();
    RegisterWindow();
    RegisterFiles();
    RegisterSystem();
    RegisterSurfaces();
    RegisterEvents();
    RegisterDraw();
    RegisterText();
    RegisterPrintf();
    RegisterPictures();
    RegisterSound();
    RegisterSprites();
    RegisterInput();
    RegisterMusic();
    RegisterNativeRoutines();
}

ScnVm::~ScnVm() = default;

OpHandler ScnVm::HandlerFor(int op) const
{
    if (OldEngine())
        if (auto found = m_handlers234.find(op); found != m_handlers234.end())
            return found->second;
    if (EngineVersion < 240)
        if (auto found = m_handlersBefore240.find(op); found != m_handlersBefore240.end())
            return found->second;
    return m_handlers[size_t(op & 0xFFFF)];
}

// ---- Memory ----

uint8_t* ScnVm::Bytes(int address, int length)
{
    if (uint64_t(uint32_t(address)) + uint64_t(int64_t(length)) > uint64_t(ScnAddressSpace::Size))
        throw ScnException(std::format("{} bytes at {:08X} run past the top of script memory", length, uint32_t(address)), -1, address, 0);
    return m_memory->Block(uint32_t(address), length);
}

// Blocks go a piece of memory at a time (to the top of the 4 GB); past the top they go on at
// address 0, as 32-bit addresses do

void ScnVm::WriteBytes(int address, const uint8_t* bytes, int length)
{
    while (length > 0)
    {
        int n = InPiece(address, length);
        std::memcpy(Bytes(address, n), bytes, size_t(n));
        bytes += n;
        length -= n;
        address += n;
    }
}

void ScnVm::ReadBytes(int address, uint8_t* bytes, int length)
{
    while (length > 0)
    {
        int n = InPiece(address, length);
        std::memcpy(bytes, Bytes(address, n), size_t(n));
        bytes += n;
        length -= n;
        address += n;
    }
}

openshiina::Bytes ScnVm::ReadBytes(int address, int count)
{
    openshiina::Bytes bytes(size_t(std::max(count, 0)));
    ReadBytes(address, bytes.data(), int(bytes.size()));
    return bytes;
}

void ScnVm::FillMemory(int dst, int count, uint8_t value)
{
    while (count > 0)
    {
        int n = InPiece(dst, count);
        std::memset(Bytes(dst, n), value, size_t(n));
        count -= n;
        dst += n;
    }
}

void ScnVm::CopyMemory(int dst, int src, int count)
{
    if (count <= 0)
        return;
    bool overlap = int64_t(dst) < int64_t(src) + count && int64_t(src) < int64_t(dst) + count;
    if (!overlap || count <= 1 << 20)
    {
        MoveMemory(dst, src, count);
        return;
    }
    constexpr int Chunk = 1 << 16;
    for (int done = 0; done < count; done += Chunk)
    {
        int n = std::min(Chunk, count - done);
        WriteBytes(dst + done, ReadBytes(src + done, n));
    }
}

void ScnVm::MoveMemory(int dst, int src, int count)
{
    if (count <= 0 || dst == src)
        return;
    if (IsContiguous(src, count) && IsContiguous(dst, count))
    {
        uint8_t* from = m_memory->Block(uint32_t(src), count);
        uint8_t* to = m_memory->Block(uint32_t(dst), count);
        std::memmove(to, from, size_t(count));
        return;
    }
    // across the top of the 4 GB: through a buffer
    WriteBytes(dst, ReadBytes(src, count));
}

std::string ScnVm::ReadString(int address)
{
    std::string bytes;
    for (uint8_t c; (c = ReadByte(address)) != 0 && bytes.size() < 0x10000; address++)
        bytes += char(c);
    return bytes;
}

int ScnVm::StringLength(int address)
{
    int n = 0;
    while (ReadByte(address + n) != 0 && n < 0x1000000)
        n++;
    return n;
}

void ScnVm::WriteString(int address, const std::string& text)
{
    WriteBytes(address, reinterpret_cast<const uint8_t*>(text.data()), int(text.size()));
    WriteByte(address + int(text.size()), 0);
}

// The heap (GlobalAlloc / VirtualAlloc of the engine): blocks in use by address with their size,
// and the free ranges below the top, merged with their neighbours. Free memory is kept zero -
// pages wholly inside a free range are dropped, the rest cleared - so a block is zero when it is
// handed out, from a free range or from the top.

int64_t ScnVm::HeapFree()
{
    return int64_t(CodeRegion) - HeapRegion + (LargeAddressAware() ? int64_t(HighHeapEnd) - HighHeapRegion : 0) - HeapInUse;
}

bool ScnVm::LargeAddressAware()
{
    if (!m_largeAddressAware)
        m_largeAddressAware = ReadLargeAddressAware();
    return *m_largeAddressAware;
}

bool ScnVm::ReadLargeAddressAware()
{
    if (auto exe = ExeFileName())
    {
        openshiina::Bytes bytes;
        if (m_host.ReadLooseFile(*exe, bytes) || m_host.ExecutableCopy(*exe, bytes))
            if (auto characteristics = PeCharacteristics(bytes))
                return (*characteristics & 0x20) != 0;
    }
    auto* formats = archives::FormatManager::Instance();
    auto scheme = formats ? formats->GetScheme(Game) : nullptr;
    return scheme && scheme->LargeAddressAware;
}

std::optional<int> ScnVm::PeCharacteristics(const openshiina::Bytes& exe)
{
    if (exe.size() <= 0x40 || exe[0] != 'M' || exe[1] != 'Z')
        return std::nullopt;
    int pe = int(LittleEndian::ToUInt32(&exe[0x3C]));
    if (pe <= 0 || pe > int(exe.size()) - 24 || std::memcmp(&exe[size_t(pe)], "PE\0\0", 4) != 0)
        return std::nullopt;
    return int(LittleEndian::ToUInt16(&exe[size_t(pe) + 22]));
}

int ScnVm::Allocate(int bytes)
{
    int at = TryAllocate(bytes);
    if (at == 0)
        throw ScnException(std::format("Out of script memory: {} MB in use, {} bytes asked for", HeapInUse >> 20, bytes), -1, 0, 0);
    return at;
}

std::optional<std::pair<int, int>> ScnVm::BlockAt(int address) const
{
    for (const auto& [start, size] : m_blocks)
        if (uint32_t(address - start) < uint32_t(size))
            return std::pair(start, size);
    return std::nullopt;
}

std::optional<int> ScnVm::NextBlock(int address) const
{
    std::optional<int> next;
    for (const auto& [start, size] : m_blocks)
        if (uint32_t(start) >= uint32_t(address) && (!next || uint32_t(start) < uint32_t(*next)))
            next = start;
    return next;
}

int ScnVm::TryAllocate(int bytes)
{
    if (bytes < 0 || int64_t(bytes) > int64_t(HighHeapEnd) - HighHeapRegion)
        return 0;
    int size = (std::max(bytes, 4) + 15) & ~15;
    // The smallest free range it fits in
    int best = -1, bestSize = 0x7FFFFFFF;
    for (const auto& [start, length] : m_freeRanges)
        if (length >= size && length < bestSize)
        {
            best = start;
            bestSize = length;
            if (length == size)
                break;
        }
    int at;
    if (best >= 0)
    {
        m_freeRanges.erase(best);
        if (bestSize > size)
            m_freeRanges[best + size] = bestSize - size;
        at = best;
    }
    // The heap must not grow into the code of the modules; then the high heap, for a large
    // address aware game
    else if (int64_t(m_heapTop) + size <= CodeRegion)
    {
        at = m_heapTop;
        m_heapTop = at + size;
    }
    else if (int64_t(uint32_t(m_highTop)) + size <= int64_t(HighHeapEnd) && LargeAddressAware())
    {
        at = m_highTop;
        m_highTop = int(uint32_t(at) + uint32_t(size));
    }
    else
        return 0;
    m_blocks[at] = size;
    HeapInUse += size;
    return at;
}

void ScnVm::Free(int address)
{
    auto found = m_blocks.find(address);
    if (found == m_blocks.end())
        return;
    int size = found->second;
    m_blocks.erase(found);
    HeapInUse -= size;
    int start = address, end = address + size;
    ClearRange(start, end);
    // Merge with the free ranges before and after
    int before = LowerFreeRange(start);
    if (before != -1)
    {
        auto range = m_freeRanges.find(before);
        if (range->first + range->second == start)
        {
            start = range->first;
            m_freeRanges.erase(range);
        }
    }
    if (auto after = m_freeRanges.find(end); after != m_freeRanges.end())
    {
        end += after->second;
        m_freeRanges.erase(after);
    }
    if (end == m_heapTop)
        m_heapTop = start;      // the top comes down; it stays zero
    else if (end == m_highTop)
        m_highTop = start;
    else
        m_freeRanges[start] = end - start;
}

int ScnVm::LowerFreeRange(int address) const
{
    // The last free range starting before the address (the key; -1 when none)
    auto found = m_freeRanges.lower_bound(address);
    if (found == m_freeRanges.begin())
        return -1;
    return std::prev(found)->first;
}

int ScnVm::AllocateCopy(const uint8_t* data, int length)
{
    int at = Allocate(length + 1);
    WriteBytes(at, data, length);
    return at;
}

void ScnVm::Push(ScnContext& c, int value)
{
    if (c.Sp <= 0)
        throw Error(c, "Not Enough Stack Area");
    c.Sp--;
    Write32(StackAddress(c.Slot, c.Sp), value);
}

int ScnVm::Pop(ScnContext& c)
{
    if (c.Sp >= StackSize)
        throw Error(c, "Not Stack Data");
    return Read32(StackAddress(c.Slot, c.Sp++));
}

// ---- Modules and tasks ----

bool ScnVm::LoadModule(int slot, const std::string& file, bool start)
{
    openshiina::Bytes code;
    if (!m_host.ReadFile(file, code))
        return false;
    int at = m_codeTop, size = (int(code.size()) + 0xFFF + 16) & ~0xFFF;
    // A large address aware game's code stops at its high heap
    if (int64_t(uint32_t(at)) + size > int64_t(HighHeapRegion) && LargeAddressAware())
        throw ScnException(std::format("Out of module memory: {} MB of modules loaded, {} asked for",
                                       (uint32_t(at) - CodeRegion) >> 20, Text::Cp932ToUtf8(file)), -1, 0, 0);
    m_codeTop += size;
    WriteBytes(at, code);
    RememberModule(at, file, code);
    auto& c = m_slots[size_t(slot)];
    c.CodeBase = c.Base = c.Entry = c.Pc = at;
    c.CodeSize = int(code.size());
    c.Loaded = true;
    c.Parent = -1;
    c.Sp = StackSize;
    c.FrameTop = 0;
    c.Flags = start ? 1 : 0;
    TasksChanged();
    // Decoded code of an older module at this address no longer applies
    for (auto it = m_decoded.begin(); it != m_decoded.end();)
        if (it->first >= at && it->first < at + int(code.size()) + 16)
        {
            Retire(std::move(it->second));
            it = m_decoded.erase(it);
        }
        else
            ++it;
    ForgetRoutines();
    return true;
}

bool ScnVm::RunFrame(int maxRounds)
{
    m_frameNumber++;
    MakePages();
    PumpGraphMovies();
    StepMusicFades();
    // The window is shown at start: all of it waits for its first WM_PAINT
    if (m_window.empty())
        InvalidateWindow();
    PumpMessages();
    FrameShown = false;
    m_taskWaited = false;
    FrameRounds = 0;
    SleepRequested = 0;
    for (int round = 0; round < maxRounds && !QuitRequested; round++)
    {
        FrameRounds = round + 1;
        m_textWentOn = false;
        RunRound();
        FireTimers();
        PumpMessages();
        // 2.34 shows its pictures as it goes and takes time only where a task waits: a frame ends
        // there, not at the first picture
        if ((OldEngine() ? m_taskWaited : FrameShown) && !m_textWentOn)
            break;
    }
    return !QuitRequested;
}

void ScnVm::RunRound()
{
    m_rounds++;
    int active = ActiveCount();
    for (int i = 0; i < active && !QuitRequested; i++)
    {
        auto& c = m_slots[size_t(i)];
        if ((c.Flags & 2) != 0)
            continue;
        if ((c.Flags & 8) != 0)
        {
            // the task's 0083 text: one step a round, the task goes on when it is done
            StepTaskText(c);
            continue;
        }
        if ((c.Flags & 1) == 0)
            continue;
        if (Run(c) == 3)
            m_taskWaited = true;
    }
}

int ScnVm::ActiveCount()
{
    if (m_activeCount >= 0)
        return m_activeCount;
    int last = -1;
    for (int i = 0; i < Slots; i++)
        if ((m_slots[size_t(i)].Flags & 1) != 0)
            last = i;
    return m_activeCount = last + 1;
}

int ScnVm::Run(ScnContext& c)
{
    CurrentTask = &c;
    std::unique_ptr<ScnInstruction> transient;
    struct Depth
    {
        int& depth;
        explicit Depth(int& d) : depth(d) { depth++; }
        ~Depth() { depth--; }
    } depth(m_runDepth);
    for (int count = 1;; count++)
    {
        // No instruction runs now that a decoded instruction taken out could still be
        if (m_runDepth == 1)
            m_retired.clear();
        int at = c.Pc;
        const auto& ins = Decode(c, at, transient);
        c.Current = at;
        c.Pc = at + ins.Length;
        if (HotSpots)
            (*HotSpots)[HotSpot(c.Slot, c.Base, at - c.Base, ins.Op)]++;
        OpCounts[size_t(ins.Op & 0xFFFF)]++;
        int result = Execute(c, ins);
        if (result == 2)
            throw Error(c, std::format("Script error in opcode {:04X}", ins.Op));
        if (result != 0)
        {
            if (result == 1)
                QuitRequested = true;
            return result;
        }
        if ((c.Flags & 1) == 0)
            return 0;
        // 0083 text: the task waits until the main loop has drawn it, a step a round
        if ((c.Flags & 8) != 0)
            return 0;
        if (ins.Op == 0x0034 || YieldEveryInstruction || count >= Budget)
            return 0;
    }
}

int ScnVm::Execute(ScnContext& c, const ScnInstruction& ins)
{
    OpHandler handler = ins.Handler ? ins.Handler : HandlerFor(ins.Op);
    if (!handler)
        throw Error(c, std::format("Opcode {:04X} is not supported yet", ins.Op));
    return handler(*this, c, ins);
}

ScnException ScnVm::Error(const ScnContext& c, const std::string& message)
{
    return ScnException(std::format("{} (slot {}, {:05X} in its module)", message, c.Slot, c.Current - c.CodeBase), c.Slot, c.Current,
                        Read16(c.Current));
}

// ---- Decoding ----

const ScnInstruction& ScnVm::Decode(ScnContext& c, int at, std::unique_ptr<ScnInstruction>& transient)
{
    if (auto cached = m_decoded.find(at); cached != m_decoded.end())
        return *cached->second;
    int p = at;
    int op = Read16(p);
    p += 2;
    const auto* layout = m_opcodes.Layout(op);
    if (!layout)
        throw ScnException(std::format("Unknown opcode {:04X} (slot {}, {:05X})", op, c.Slot, at - c.Base), c.Slot, at, op);
    auto ins = std::make_unique<ScnInstruction>();
    auto& args = ins->Args;
    auto& raw = ins->Raw;
    auto& targets = ins->Targets;
    auto& caseValues = ins->CaseValues;
    bool cache = true;
    auto isConstant = [](const ScnOperand& o) { return o.Kind == 4 && !o.Relative && !o.AddressOf; };
    for (const auto& item : *layout)
    {
        switch (item.kind)
        {
        case LayoutItem::Sprite234:
        {
            // The engine reads the flags first and then as many more as they ask for: a constant
            // decides once, a variable as it is now (and the instruction is decoded again next time)
            for (int k = 0; k < 7; k++)
                args.push_back(ReadOperand(p));
            ScnOperand f = args[2];
            bool constant = isConstant(f);
            cache = constant;
            int flags = constant ? f.Value : Value(c, f);
            bool alpha = (flags & 0x60000000) != 0, tint = (flags & 0x20000000) != 0;
            raw.push_back(alpha ? 1 : 0);
            raw.push_back(tint ? 1 : 0);
            if (alpha)
                args.push_back(ReadOperand(p));
            if (tint)
                for (int k = 0; k < 3; k++)
                    args.push_back(ReadOperand(p));
            break;
        }
        case LayoutItem::Glyph250:
        {
            // index, text, then w, h, pixels only for a text
            args.push_back(ReadOperand(p));
            args.push_back(ReadOperand(p));
            ScnOperand t = args[1];
            bool constant = isConstant(t);
            cache = constant;
            if ((constant ? t.Value : Value(c, t)) != 0)
                for (int k = 0; k < 3; k++)
                    args.push_back(ReadOperand(p));
            break;
        }
        case LayoutItem::Operand:
            args.push_back(ReadOperand(p));
            break;
        case LayoutItem::Counted:
        {
            int count = Read16(p);
            p += 2;
            raw.push_back(count);
            for (int i = 0; i < count; i++)
                args.push_back(ReadOperand(p, op == 0x03CE || op == 0x03CF));
            break;
        }
        case LayoutItem::Switch:
        {
            // u32 end of the table (relative), index, targets up to the end
            int end = Read32(p);
            p += 4;
            raw.push_back(end);
            args.push_back(ReadOperand(p));
            while (p - c.Base < end && targets.size() < 0x10000)
                targets.push_back(ReadOperand(p).Value);
            break;
        }
        case LayoutItem::Case:
        {
            // u32 address of the index operand, u32 target, values, FF, u32 next case
            int target = Read32(p + 4);
            p += 8;
            while (ReadByte(p) != 0xFF)
                caseValues.push_back(ReadOperand(p).Value);
            p++;
            raw.push_back(target);
            raw.push_back(Read32(p));
            p += 4;
            break;
        }
        case LayoutItem::UpToFF:
            while (ReadByte(p) != 0xFF)
                args.push_back(ReadOperand(p));
            p++;
            break;
        case LayoutItem::UpToMinus1:
            // Operands up to the first -1 (the scripts end the list with a constant)
            while (true)
            {
                ScnOperand o = ReadOperand(p);
                args.push_back(o);
                if ((isConstant(o) && o.Value == -1) || args.size() >= 0x100)
                    break;
            }
            break;
        case LayoutItem::RawBytes:
        {
            int n = item.size;
            raw.push_back(n == 1 ? ReadByte(p) : n == 2 ? Read16(p) : n == 4 ? Read32(p) : 0);
            p += n;
            break;
        }
        }
    }
    ins->Op = op;
    ins->Address = at;
    ins->Length = p - at;
    ins->Handler = HandlerFor(op);
    if (!cache)
    {
        transient = std::move(ins);
        return *transient;
    }
    auto& kept = m_decoded[at];
    kept = std::move(ins);
    return *kept;
}

ScnOperand ScnVm::ReadOperand(int& p, bool declaration)
{
    int at = p;
    uint8_t type = ReadByte(p++);
    bool addressOf = (type & 0x40) != 0;
    bool relative = (type & 0x80) != 0;
    uint8_t kind = uint8_t(addressOf ? type & 0x3F : type & 0x7F);
    ScnOperand o{kind, relative, addressOf, 0, {}};
    if (kind >= 2 && kind <= 15 && kind != 4 && kind != 5)
    {
        o.Value = Read16(p);
        p += 2;
        return o;
    }
    if (kind == 4 || kind == 5)
    {
        o.Value = Read32(p);
        p += 4;
        return o;
    }
    if (kind == 0x10)
    {
        // The value of a string operand is its address
        o.Value = p;
        while (ReadByte(p) != 0)
            p++;
        p++;
        return o;
    }
    if (kind == 0x11)
    {
        bool integer = ReadByte(p) == 0x2E;
        if (integer)
            p++;
        int start = p;
        while (ReadByte(p) != 0)
            p++;
        auto text = ReadBytes(start, p - start);
        o.Text.assign(text.begin(), text.end());
        p++;
        // Value 1 marks the "." form, whose result is the expression's value
        o.Value = integer ? 1 : 0;
        return o;
    }
    if (kind == 0x12 || kind == 0x13)
    {
        // A name up to its 0 or a '}'; 03CE / 03CF read their names to the 0. 2.34 goes on to the
        // 0 for every name and names it up to the first '}'
        bool whole = declaration && !OldEngine();
        int start = p;
        while (ReadByte(p) != 0 && (whole || ReadByte(p) != uint8_t('}')))
            p++;
        auto name = ReadBytes(start, p - start);
        o.Text.assign(name.begin(), name.end());
        if (OldEngine())
            while (ReadByte(p) != 0)
                p++;
        p++;
        return o;
    }
    throw ScnException(std::format("Operand type {:02X} at {:X} is not supported", type, at), -1, at, 0);
}

// ---- Operands (GETV / SETV / GETADR of the executable) ----

int ScnVm::AddressOf(ScnContext& c, const ScnOperand& o)
{
    switch (o.Kind & 0x3E)
    {
    case 2: return GAddress(o.Value);
    case 4: return c.Base + o.Value;
    case 6: return SAddress(o.Value);
    case 8: return StackAddress(c.Slot, c.Sp + o.Value);
    case 10: return AAddress(o.Value);
    case 12: return FAddress(c.Slot, o.Value);
    case 14: return BAddress(o.Value);
    case 0x12: return NamedAddress(c, o.Text);
    default: throw Error(c, std::format("Script Error (GetVarAdr): operand type {:02X}", o.Kind));
    }
}

int ScnVm::Value(ScnContext& c, const ScnOperand& o)
{
    if (o.AddressOf)
        return AddressOf(c, o);
    int rel = o.Relative ? c.Base : 0;
    switch (o.Kind)
    {
    case 4: return o.Value + rel;
    case 5: return Read32(o.Value + rel);
    case 6: return ReadByte(SAddress(o.Value)) + rel;
    case 7: return ReadByte(ReadByte(SAddress(o.Value)) + rel);
    case 0x10: return o.Value;
    case 0x11: return Calculate(c, o.Text, o.Value == 1);
    case 0x12: return Read32(NamedAddress(c, o.Text)) + rel;
    case 0x13: return Read32(Read32(NamedAddress(c, o.Text)) + rel);
    }
    // AddressOf looks at the kind without its dereference bit
    int value = Read32(AddressOf(c, o));
    return (o.Kind & 1) == 0 ? value + rel : Read32(value + rel);
}

void ScnVm::Store(ScnContext& c, const ScnOperand& o, int value)
{
    int rel = o.Relative ? c.Base : 0;
    switch (o.Kind)
    {
    case 4:
    case 0x10:
    case 0x11:
        return;     // constants take nothing
    case 5:
        Write32(o.Value + rel, value);
        return;
    case 6:
        WriteByte(SAddress(o.Value), uint8_t(value & 1));
        return;
    case 7:
        WriteByte(ReadByte(SAddress(o.Value)) + rel, uint8_t(value & 1));
        return;
    case 0x12:
        Write32(NamedAddress(c, o.Text), value);
        return;
    case 0x13:
        Write32(Read32(NamedAddress(c, o.Text)) + rel, value);
        return;
    }
    int address = AddressOf(c, o);
    if ((o.Kind & 1) == 0)
        Write32(address, value);
    else
        Write32(Read32(address) + rel, value);
}

// ---- Shift-JIS text ----

std::string SjisFold(const std::string& text)
{
    return Text::FoldCase(Text::Cp932ToUtf8(text));
}

bool SjisEqualsIgnoreCase(const std::string& a, const std::string& b)
{
    return SjisFold(a) == SjisFold(b);
}

bool SjisEndsWithIgnoreCase(const std::string& text, const std::string& ending)
{
    std::string t = SjisFold(text), e = SjisFold(ending);
    return t.size() >= e.size() && t.compare(t.size() - e.size(), e.size(), e) == 0;
}

bool SjisStartsWithIgnoreCase(const std::string& text, const std::string& start)
{
    std::string t = SjisFold(text), s = SjisFold(start);
    return t.compare(0, s.size(), s) == 0;
}

std::string SjisTrim(const std::string& text)
{
    auto white = [&](size_t at, size_t& length) {
        uint8_t b = uint8_t(text[at]);
        if (b == ' ' || (b >= 0x09 && b <= 0x0D))
        {
            length = 1;
            return true;
        }
        if (b == 0x81 && at + 1 < text.size() && uint8_t(text[at + 1]) == 0x40)
        {
            length = 2;
            return true;
        }
        return false;
    };
    size_t start = 0, length = 0;
    while (start < text.size() && white(start, length))
        start += length;
    // From the end: walk the characters from the start (a trail byte may look like a space)
    size_t end = start, at = start;
    while (at < text.size())
    {
        uint8_t b = uint8_t(text[at]);
        size_t n = ((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC)) && at + 1 < text.size() ? 2 : 1;
        if (!white(at, length))
            end = at + n;
        at += n;
    }
    return text.substr(start, end - start);
}

namespace {

bool IsLead(uint8_t b) { return (b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC); }

} // namespace

size_t SjisFind(const std::string& text, char c, size_t from)
{
    for (size_t at = 0; at < text.size(); at += IsLead(uint8_t(text[at])) && at + 1 < text.size() ? 2 : 1)
        if (at >= from && text[at] == c)
            return at;
    return std::string::npos;
}

size_t SjisFindLast(const std::string& text, char c)
{
    size_t found = std::string::npos;
    for (size_t at = 0; at < text.size(); at += IsLead(uint8_t(text[at])) && at + 1 < text.size() ? 2 : 1)
        if (text[at] == c)
            found = at;
    return found;
}

bool NetTryParseInt(std::string_view text, int& value)
{
    auto white = [](char ch) { return ch == ' ' || (ch >= '\t' && ch <= '\r'); };
    size_t at = 0, end = text.size();
    while (at < end && white(text[at]))
        at++;
    while (end > at && white(text[end - 1]))
        end--;
    bool negative = false;
    if (at < end && (text[at] == '-' || text[at] == '+'))
        negative = text[at++] == '-';
    if (at == end)
        return false;
    int64_t v = 0;
    for (; at < end; at++)
    {
        if (text[at] < '0' || text[at] > '9')
            return false;
        v = v * 10 + (text[at] - '0');
        if (v > int64_t(0x80000000))
            return false;
    }
    if (negative)
        v = -v;
    if (v > 0x7FFFFFFF)
        return false;
    value = int(v);
    return true;
}

} // namespace openshiina::scripting
