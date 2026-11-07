// C# Scripting/ScnVm.Gdi.cs: calls into Windows DLLs made by the scripts (op_01A4, FUN_0041DB50 ->
// FUN_0041DA80: LoadLibraryA + GetProcAddress, the arguments up to an FF byte pushed as dwords).
// The functions the games call are done here: the GDI ones draw into the engine's surfaces through
// their device contexts (0x00030000 + surface, op_0529), with brushes and pens kept here and the
// shapes drawn by the platform (IScnShapes). Also the font list of op_006F.
#include "scripting/ScnVm.h"

#include <algorithm>
#include <stdexcept>

#include "text/Text.h"

namespace openshiina::scripting {

namespace {

constexpr int WhiteBrush = 0x00050000, BlackPen = 0x00050001, NullBrush = 0x00050002, NullPen = 0x00050003;
constexpr int ResourceHandles = 0x00080000;

/// A DLL's name as 01A4 looks it up: lower case, without ".dll".
std::string DllName(const std::string& dll)
{
    std::string lower;
    for (size_t k = 0; k < dll.size(); k++)
    {
        uint8_t b = uint8_t(dll[k]);
        lower += char(b >= 'A' && b <= 'Z' ? b + 32 : b);
        // A Shift-JIS character's second byte stays as it is
        if (((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC)) && k + 1 < dll.size())
            lower += dll[++k];
    }
    std::string name;
    for (size_t k = 0; k < lower.size();)
        if (lower.compare(k, 4, ".dll") == 0)
            k += 4;
        else
            name += lower[k++];
    return name;
}

void AppendUtf16(std::u16string& out, uint32_t c)
{
    if (c >= 0x10000)
    {
        c -= 0x10000;
        out += char16_t(0xD800 + (c >> 10));
        out += char16_t(0xDC00 + (c & 0x3FF));
    }
    else
        out += char16_t(c);
}

/// UTF-8 to UTF-16; a broken sequence gives U+FFFD.
std::u16string Utf8ToUtf16(std::string_view utf8)
{
    std::u16string out;
    for (size_t i = 0; i < utf8.size();)
    {
        uint8_t b = uint8_t(utf8[i]);
        int extra = b < 0x80 ? 0 : (b & 0xE0) == 0xC0 ? 1 : (b & 0xF0) == 0xE0 ? 2 : (b & 0xF8) == 0xF0 ? 3 : -1;
        uint32_t c = extra == 0 ? b : extra == 1 ? b & 0x1F : extra == 2 ? b & 0x0F : b & 0x07;
        size_t k = 1;
        for (; extra > 0 && k <= size_t(extra) && i + k < utf8.size() && (uint8_t(utf8[i + k]) & 0xC0) == 0x80; k++)
            c = c << 6 | (uint8_t(utf8[i + k]) & 0x3F);
        bool whole = extra >= 0 && k == size_t(extra) + 1;
        bool shortest = extra <= 0 || (extra == 1 && c >= 0x80) || (extra == 2 && c >= 0x800) || (extra == 3 && c >= 0x10000);
        if (!whole || !shortest || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF))
            c = 0xFFFD;
        AppendUtf16(out, c);
        i += whole ? k : std::max<size_t>(k, 1);
    }
    return out;
}

/// UTF-16 to UTF-8; a lone surrogate gives U+FFFD.
std::string Utf16ToUtf8(const std::u16string& text)
{
    std::string out;
    for (size_t i = 0; i < text.size(); i++)
    {
        uint32_t c = text[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < text.size() && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF)
            c = 0x10000 + ((c - 0xD800) << 10) + (text[++i] - 0xDC00);
        else if (c >= 0xD800 && c <= 0xDFFF)
            c = 0xFFFD;
        if (c < 0x80)
            out += char(c);
        else if (c < 0x800)
        {
            out += char(0xC0 | c >> 6);
            out += char(0x80 | (c & 0x3F));
        }
        else if (c < 0x10000)
        {
            out += char(0xE0 | c >> 12);
            out += char(0x80 | (c >> 6 & 0x3F));
            out += char(0x80 | (c & 0x3F));
        }
        else
        {
            out += char(0xF0 | c >> 18);
            out += char(0x80 | (c >> 12 & 0x3F));
            out += char(0x80 | (c >> 6 & 0x3F));
            out += char(0x80 | (c & 0x3F));
        }
    }
    return out;
}

/// A Windows code page: the system's ones (0 ANSI, 1 OEM, 3 the thread's) are Japanese, 932.
bool IsCp932(int codePage) { return codePage == 0 || codePage == 1 || codePage == 3 || codePage == 932; }

std::u16string DecodeCodePage(int codePage, const openshiina::Bytes& bytes)
{
    std::string_view raw(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    if (IsCp932(codePage))
        return Utf8ToUtf16(Text::Cp932ToUtf8(raw));
    if (codePage == 65001)
        return Utf8ToUtf16(raw);
    throw std::runtime_error("Code page " + std::to_string(codePage) + " is not supported yet");
}

std::string EncodeCodePage(int codePage, const std::u16string& text)
{
    if (IsCp932(codePage))
        return Text::Utf8ToCp932(Utf16ToUtf8(text));
    if (codePage == 65001)
        return Utf16ToUtf8(text);
    throw std::runtime_error("Code page " + std::to_string(codePage) + " is not supported yet");
}

/// A bounds-checked little-endian read of a PE file (the C# player's BitConverter throws).
struct PeReader
{
    const openshiina::Bytes& Pe;
    int I32(int at) const
    {
        if (at < 0 || size_t(at) + 4 > Pe.size())
            throw std::out_of_range("pe");
        return int(LittleEndian::ToUInt32(Pe.data() + at));
    }
    int U16(int at) const
    {
        if (at < 0 || size_t(at) + 2 > Pe.size())
            throw std::out_of_range("pe");
        return LittleEndian::ToUInt16(Pe.data() + at);
    }
};

} // namespace

std::pair<openshiina::Bytes, int>* ScnVm::Resource(int handle)
{
    if (handle > ResourceHandles && handle <= ResourceHandles + int(m_resources.size()))
        return &m_resources[size_t(handle - ResourceHandles - 1)];
    return nullptr;
}

std::optional<openshiina::Bytes> ScnVm::ExeResource(int name, int type)
{
    // The resource of the game's executable with the type and name numbers given (its first
    // language), read from the file as data, or none
    auto exe = ExeFileName();
    openshiina::Bytes pe;
    if (!exe || !m_host.ReadLooseFile(*exe, pe) || pe.size() <= 0x40)
        return std::nullopt;
    PeReader r{pe};
    try
    {
        int header = r.I32(0x3C);
        int sections = r.U16(header + 6), optional = header + 0x18;
        int rootRva = r.I32(optional + 96 + 2 * 8);
        auto offset = [&](int rva) {
            for (int s = 0, at = optional + r.U16(header + 0x14); s < sections; s++, at += 40)
            {
                int va = r.I32(at + 12), size = std::max(r.I32(at + 8), r.I32(at + 16));
                if (rva >= va && rva < va + size)
                    return rva - va + r.I32(at + 20);
            }
            return -1;
        };
        int root = offset(rootRva);
        // A directory's entry for an id (numbers come after the named ones), or the first
        auto entry = [&](int directory, std::optional<int> id) -> std::optional<int> {
            int named = r.U16(directory + 12), ids = r.U16(directory + 14);
            for (int e = 0; e < named + ids; e++)
            {
                int at = directory + 16 + 8 * e;
                if (!id || r.I32(at) == *id)
                    return r.I32(at + 4);
            }
            return std::nullopt;
        };
        if (root < 0)
            return std::nullopt;
        auto types = entry(root, type);
        if (!types || *types >= 0)
            return std::nullopt;
        auto names = entry(root + (*types & 0x7FFFFFFF), name);
        if (!names || *names >= 0)
            return std::nullopt;
        auto language = entry(root + (*names & 0x7FFFFFFF), std::nullopt);
        if (!language || *language < 0)
            return std::nullopt;
        int data = root + *language, at = offset(r.I32(data)), length = r.I32(data + 4);
        if (at < 0 || length < 0 || int64_t(at) + length > int64_t(pe.size()))
            return std::nullopt;
        return openshiina::Bytes(pe.begin() + at, pe.begin() + at + length);
    }
    catch (const std::out_of_range&)
    {
        return std::nullopt;
    }
}

void ScnVm::RegisterGdi()
{
    m_gdiObjects[WhiteBrush] = GdiObject{false, 0xFFFFFF, 0, 0};
    m_gdiObjects[BlackPen] = GdiObject{true, 0, 0, 1};
    m_gdiObjects[NullBrush] = GdiObject{false, 0, 1, 0};
    m_gdiObjects[NullPen] = GdiObject{true, 0, 5, 1};

    // 01A4 -, dll, function, arguments..., v: v = dll!function(arguments)
    Register(0x01A4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        std::string dll = vm.ReadString(vm.Value(c, i.Args[1])), function = vm.ReadString(vm.Value(c, i.Args[2]));
        std::vector<int> a(i.Args.size() - 4);
        for (size_t k = 0; k < a.size(); k++)
            a[k] = vm.Value(c, i.Args[3 + k]);
        auto result = vm.DllCall(DllName(dll), function, a);
        if (!result)
            throw vm.Error(c, "The scripts call " + Text::Cp932ToUtf8(dll) + "!" + Text::Cp932ToUtf8(function) + ", which is not supported yet");
        vm.Store(c, i.Args.back(), *result);
        return 0;
    });
    // 006F -, v: v = a block of 32-byte face names, an empty one last (FUN_00415630)
    Register(0x006F, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        std::vector<std::string> faces;
        if (auto* fonts = vm.m_host.Fonts())
            faces = fonts->FixedPitchJapaneseFaces();
        int block = vm.Allocate((int(faces.size()) + 1) * 32);
        for (size_t k = 0; k < faces.size(); k++)
            vm.WriteBytes(block + 32 * int(k), reinterpret_cast<const uint8_t*>(faces[k].data()), std::min(int(faces[k].size()), 31));
        vm.Store(c, i.Args[1], block);
        return 0;
    });
}

std::optional<int> ScnVm::DllCall(const std::string& dll, const std::string& function, const std::vector<int>& a)
{
    auto Arg = [&](size_t k) { return k < a.size() ? a[k] : 0; };
    auto is = [&](const char* d, const char* f) { return dll == d && function == f; };

    if (is("gdi32", "CreateSolidBrush"))
        return NewGdiObject(GdiObject{false, Arg(0) & 0xFFFFFF, 0, 0});
    if (is("gdi32", "CreatePen"))
        return NewGdiObject(GdiObject{true, Arg(2) & 0xFFFFFF, Arg(0), Arg(1)});
    if (is("gdi32", "SelectObject"))
    {
        int dc = Arg(0), handle = Arg(1);
        auto o = m_gdiObjects.find(handle);
        if (o == m_gdiObjects.end())
            return 0;
        auto selected = m_gdiSelected.find(dc);
        auto [brush, pen] = selected != m_gdiSelected.end() ? selected->second : std::pair(WhiteBrush, BlackPen);
        m_gdiSelected[dc] = o->second.Pen ? std::pair(brush, handle) : std::pair(handle, pen);
        return o->second.Pen ? pen : brush;
    }
    if (is("gdi32", "DeleteObject"))
        return Arg(0) >= 0x00060000 && m_gdiObjects.erase(Arg(0)) != 0 ? 1 : 0;
    if (is("gdi32", "Ellipse"))
        return GdiEllipse(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4)) ? 1 : 0;
    if (is("user32", "GetForegroundWindow"))
        return m_host.Active() ? WindowHandle : 0;
    if (is("user32", "AdjustWindowRect"))
        // The host makes the window around the picture; the rectangle stays as it is
        return 1;
    if (is("kernel32", "CreateDirectoryA"))
    {
        // Ao no Juuai's save folder ("save" in the game's folder): the host keeps its saves in a
        // folder of its own and never writes into the game's; a folder made in the game's folder
        // is a save folder from then on (Libra of the Vampire Princess's data_libraes\)
        FolderMade(ReadString(Arg(0)));
        return 1;
    }
    if (is("kernel32", "GlobalMemoryStatusEx"))
    {
        // MEMORYSTATUSEX of a PC with 8 GB, as a 32-bit program sees it (4 GB of address space
        // when it is large address aware); the address space free is the heap's (Libra of the
        // Vampire Princess's START takes all of it but SGMemSize with VirtualAlloc while it loads
        // its tables, then gives it back)
        int p = Arg(0);
        Write32(p + 4, 25);
        Write64(p + 8, int64_t(8) << 30);
        Write64(p + 16, int64_t(6) << 30);
        Write64(p + 24, int64_t(16) << 30);
        Write64(p + 32, int64_t(12) << 30);
        Write64(p + 40, LargeAddressAware() ? 0xFFFE0000LL : 0x7FFE0000LL);
        Write64(p + 48, HeapFree());
        Write64(p + 56, 0);
        return 1;
    }
    if (is("kernel32", "GetSystemInfo"))
    {
        // SYSTEM_INFO of an x86 PC with 4 cores: pages of 4 KB, addresses from 64 KB to 2 GB (4 GB
        // when large address aware), allocations at 64 KB (Libra of the Vampire Princess's START
        // steps its search for the largest VirtualAlloc down to the page size)
        int p = Arg(0);
        Write32(p, 0);
        Write32(p + 4, 4096);
        Write32(p + 8, 0x10000);
        Write32(p + 12, LargeAddressAware() ? int(0xFFFEFFFFu) : 0x7FFEFFFF);
        Write32(p + 16, 15);
        Write32(p + 20, 4);
        Write32(p + 24, 586);
        Write32(p + 28, 0x10000);
        Write16(p + 32, 6);
        Write16(p + 34, 0);
        return 0;
    }
    if (is("kernel32", "VirtualAlloc"))
    {
        // address, size, type, protection: a block of the heap (zeroed), 0 when there is no room
        // for it; an address given is one already allocated (a commit in it)
        if (Arg(0) != 0)
        {
            auto block = BlockAt(Arg(0));
            return block && int64_t(Arg(0)) + uint32_t(Arg(1)) <= int64_t(block->first) + block->second ? Arg(0) : 0;
        }
        int allocated = TryAllocate((Arg(1) + 0xFFF) & ~0xFFF);
        if (allocated != 0)
            m_virtualBlocks.insert(allocated);
        return allocated;
    }
    if (is("kernel32", "VirtualFree"))
    {
        // address, size, type: MEM_RELEASE (0x8000) gives the block back; MEM_DECOMMIT leaves it
        // (the memory is only taken as it is written)
        if ((Arg(2) & 0x8000) != 0)
        {
            if (!IsBlock(Arg(0)))
                return 0;
            m_virtualBlocks.erase(Arg(0));
            Free(Arg(0));
        }
        return 1;
    }
    if (is("kernel32", "VirtualQuery"))
    {
        // address, MEMORY_BASIC_INFORMATION, its size: the heap block the address is in
        // (committed, read and write, private), else the free room up to the next one (only Libra
        // of the Vampire Princess's map of the memory for RIO.INI's TestMMOut)
        int address = Arg(0), info = Arg(1);
        uint32_t top = LargeAddressAware() ? 0xFFFF0000u : 0x7FFF0000u;
        if (Arg(2) < 28 || uint32_t(address) >= top)
            return 0;
        if (auto block = BlockAt(address))
        {
            Write32(info, address & ~0xFFF);
            Write32(info + 4, block->first);
            Write32(info + 8, 4);
            Write32(info + 12, block->first + block->second - (address & ~0xFFF));
            Write32(info + 16, 0x1000);
            Write32(info + 20, 4);
            Write32(info + 24, 0x20000);
        }
        else
        {
            int start = address & ~0xFFF;
            auto next = NextBlock(address);
            uint32_t end = next ? uint32_t(*next) : top;
            Write32(info, start);
            Write32(info + 4, 0);
            Write32(info + 8, 0);
            Write32(info + 12, int(std::max(end - uint32_t(start), 0x1000u)));
            Write32(info + 16, 0x10000);
            Write32(info + 20, 1);
            Write32(info + 24, 0);
        }
        return 28;
    }
    if (is("gdi32", "AddFontMemResourceEx"))
    {
        // font file, size, -, count: the fonts of a file the scripts loaded (Libra of the Vampire
        // Princess's .ttf in its archives) for this game's text alone; a handle, 0 when none was
        // added
        auto* fonts = m_host.Fonts();
        if (Arg(0) == 0 || Arg(1) <= 0 || !fonts)
            return 0;
        openshiina::Bytes file = ReadBytes(Arg(0), Arg(1));
        int count = fonts->AddMemoryFonts(file.data(), file.size());
        if (Arg(3) != 0)
            Write32(Arg(3), count);
        return count > 0 ? 0x00070000 + ++m_memoryFonts : 0;
    }
    if (is("user32", "UpdateWindow"))
    {
        // the game window: WM_PAINT now where part of it is invalid (as 07D1); a control has
        // nothing drawn
        if (Arg(0) == 0 || Arg(0) == WindowHandle)
            Paint();
        return 1;
    }
    if (is("user32", "ShowWindow"))
        // The host shows the window; it was visible
        return 1;
    if (is("user32", "SetForegroundWindow"))
        return 1;
    if (is("user32", "GetWindowLongA"))
    {
        // window, index: a value SetWindowLongA kept, else the game window's style (visible, a
        // caption, system menu, minimize and maximize boxes) for GWL_STYLE
        auto kept = m_windowLongs.find({Arg(0), Arg(1)});
        return kept != m_windowLongs.end() ? kept->second : Arg(1) == -16 ? 0x14CB0000 : 0;
    }
    if (is("user32", "SetWindowLongA"))
    {
        // window, index, value: kept and the old one given back (Puchipuchi Idol Kouhosei takes
        // the maximize box off its style while a menu of its own is open); the host's window is
        // as the player's settings make it
        auto was = m_windowLongs.find({Arg(0), Arg(1)});
        int old = was != m_windowLongs.end() ? was->second : Arg(1) == -16 ? 0x14CB0000 : 0;
        m_windowLongs[{Arg(0), Arg(1)}] = Arg(2);
        return old;
    }
    if (is("user32", "GetClientRect"))
    {
        // The window's inside: the picture (Sorcery Jokers' hook for a window being sized)
        int r = Arg(1);
        Write32(r, 0);
        Write32(r + 4, 0);
        Write32(r + 8, ScreenWidth);
        Write32(r + 12, ScreenHeight);
        return 1;
    }
    if (is("user32", "ClientToScreen"))
        // The window's inside is at (0, 0) of the screen (09EC): the point stays
        return 1;
    if (is("kernel32", "GetDiskFreeSpaceExA"))
    {
        // A disk of 256 GB with 64 GB free, where each pointer is not null (Chuuchuu Nurse saves a
        // screenshot to its save folder when 360,054 bytes are free)
        if (Arg(1) != 0)
            Write64(Arg(1), int64_t(64) << 30);
        if (Arg(2) != 0)
            Write64(Arg(2), int64_t(256) << 30);
        if (Arg(3) != 0)
            Write64(Arg(3), int64_t(64) << 30);
        return 1;
    }
    if (is("kernel32", "GetModuleFileNameA"))
    {
        // The game's .exe (Can Fes!'s START reads it whole and compares it with a copy in
        // CANFES_D.WAR): its name in the game folder, which the file opcodes read as a loose file
        // (a full path, with its drive, they would not) - as it shipped, from that copy, when the
        // game has one (ModuleFile)
        int buffer = Arg(1), size = Arg(2);
        auto exe = ExeFileName();
        if (buffer == 0 || size <= 0 || !exe)
            return 0;
        m_moduleFile = *exe;
        int n = std::min(int(exe->size()), size - 1);
        WriteBytes(buffer, reinterpret_cast<const uint8_t*>(exe->data()), n);
        WriteByte(buffer + n, 0);
        return n;
    }
    if (is("user32", "SetMenuItemInfoW"))
        // The window has no menu of the system's here (Bitch Gakuen's START renames the items of
        // its window menu): done
        return 1;
    if (is("user32", "GetMessageExtraInfo"))
        // The message came from a mouse, not from a pen or a finger (Windows marks those
        // 0xFF5157xx; Bitch Nee-chan's hook reads it for RIO.INI's TabletShortcut)
        return 0;
    if (is("kernel32", "LoadLibraryA"))
    {
        // No library loads; one in the game's folder seems to (a handle), so that its functions
        // below answer: Bitch Nee-chan's HID_ONAHOLE.dll (a USB device sold with the game; START
        // says the DLL is missing when it does not load). Sis△Camp's plugin.dll is no file: its
        // executable makes it as it runs (the DL edition's DRM.dll); START stops without it, calls
        // its executeAPI once and frees it
        std::string name = ReadString(Arg(0));
        return m_host.LooseFileSize(name) || SjisEqualsIgnoreCase(name, "plugin.dll") ? 0x10000000 : 0;
    }
    if (function == "executeAPI")
        // (what it gives is not looked at)
        return 0;
    if (is("kernel32", "FreeLibrary"))
        // The library LoadLibraryA seemed to load: freed
        return 1;
    // HID_ONAHOLE.dll as it is without its device: version 1.0, onahoInit finds none (Libra of the
    // Vampire Princess has the DLL as LIBRAeS_D2.WAR; its START ends at a version below 1.0)
    if (function == "onahoDLLVer")
        return 0x10000;
    if (function == "onahoInit" || function == "onahoEnd" || function == "onahoSetLevel" || function == "onahoSetPattern")
        return 0;
    // Libra of the Vampire Princess's Steam DLL (LIBRAeS_S2.WAR, over steam_api.dll): the game is
    // played without Steam, so Steam starts, runs its callbacks and takes the achievements without
    // being there
    if (function == "steamInit" || function == "steamDebugMode" || function == "steamRunCallbacks" || function == "steamSetAchievement"
        || function == "steamResetAchievement" || function == "steamEnd")
        return 1;
    if (is("kernel32", "GetVersionExA"))
    {
        // OSVERSIONINFOA as Windows 8 and later give a program without a manifest: 6.2, build
        // 9200, NT (Bitch Nee-chan's START reads the major and minor version)
        int p = Arg(0);
        int size = Read32(p);
        for (int k = 4; k < std::min(size, 156); k++)
            WriteByte(p + k, 0);
        Write32(p + 4, 6);
        Write32(p + 8, 2);
        Write32(p + 12, 9200);
        Write32(p + 16, 2);
        return 1;
    }
    // The Pillagers of Raillore's START asks whether it runs on a 64-bit Windows (and goes on the
    // same way either way): kernel32 is there, IsWow64Process too, and says yes
    if (is("kernel32", "GetModuleHandleA"))
        return SjisEqualsIgnoreCase(ReadString(Arg(0)), "kernel32.dll") ? Kernel32Module : 0;
    if (is("kernel32", "GetProcAddress"))
        // (the scripts call the function through 01A4, never at this address)
        return Arg(0) == Kernel32Module && ReadString(Arg(1)) == "IsWow64Process" ? Kernel32Module + 0x1000 : 0;
    if (is("kernel32", "GetCurrentProcess"))
        return -1;
    if (is("kernel32", "IsWow64Process"))
    {
        if (Arg(1) != 0)
            Write32(Arg(1), 1);
        return 1;
    }
    // RIOSUB.DLL's DXGI presentation (The Pillagers of Raillore, asked for when the window leaves
    // full screen): it does not start, and the scripts draw as without it (rsDXGIEnd is called only
    // when it started); rsDXGIWaitForVBlank: no DXGI output, it returns at once
    if (is("riosub", "rsDXGIStart") || is("riosub", "rsDXGIEnd") || is("riosub", "rsDXGIWaitForVBlank"))
        return 0;
    // RIOSUB.DLL's colour changes of a picture's pixels (ScnVm.RioSub.cpp); what they give is not
    // looked at
    if (is("riosub", "rsS25ChangeHSL"))
    {
        RioChangeHsl(Arg(0), Arg(1), Arg(2), Arg(3), Arg(5), Arg(6));
        return 0;
    }
    if (is("riosub", "rsS25ChangeColorBalance"))
    {
        RioChangeColorBalance(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5), Arg(6), Arg(7));
        return 0;
    }
    if (is("riosub", "rsS25ChangeCBHSL"))
    {
        RioChangeCbHsl(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5), Arg(6), Arg(7), Arg(9), Arg(10));
        return 0;
    }
    if (is("riosub", "rsDialogViewText"))
    {
        // window, title, text, ?: a dialog showing a text file (Oni ga Kuru's menu loads
        // "<name>.txt" with 00C9 and shows it so): its Shift-JIS text in the host's message box,
        // with the file's name as its title
        int text = Arg(2);
        auto size = m_fileSizes.find(text);
        int length = size != m_fileSizes.end() ? size->second : StringLength(text);
        openshiina::Bytes bytes = ReadBytes(text, length);
        while (!bytes.empty() && bytes.back() == 0)
            bytes.pop_back();
        m_host.MessageBox(std::string(bytes.begin(), bytes.end()), ReadString(Arg(1)), 0);
        return 1;
    }
    if (is("kernel32", "FindResourceA"))
    {
        // module (0 or the game's own), name, type (numbers): a resource of the game's executable,
        // read from the file (Yoyogi Hitozuma Senmon Gakuin's START checks its icon 2 against
        // icon.bin of its archives); a handle, 0 when there is none
        if (Arg(1) <= 0 || Arg(1) > 0xFFFF || Arg(2) <= 0 || Arg(2) > 0xFFFF)
            return 0;
        auto resource = ExeResource(Arg(1), Arg(2));
        if (!resource)
            return 0;
        m_resources.emplace_back(std::pair(std::move(*resource), 0));
        return ResourceHandles + int(m_resources.size());
    }
    if (is("kernel32", "SizeofResource"))
    {
        // module, resource: its size
        auto* sized = Resource(Arg(1));
        return sized ? int(sized->first.size()) : 0;
    }
    if (is("kernel32", "LoadResource"))
        // module, resource: a handle LockResource takes (the same)
        return Resource(Arg(1)) ? Arg(1) : 0;
    if (is("kernel32", "LockResource"))
    {
        // handle: the resource's bytes in the scripts' memory (copied there once)
        auto* locked = Resource(Arg(0));
        if (!locked)
            return 0;
        if (locked->second == 0)
            locked->second = AllocateCopy(locked->first);
        return locked->second;
    }
    if (is("shlwapi", "PathStripPathA"))
    {
        // path: what follows its last '\', '/' or ':' moved to its start, in place (Oni ga Kuru's
        // START keeps a file's name); Shift-JIS lead bytes take their next byte
        int path = Arg(0), name = path;
        for (int p = path, b; (b = ReadByte(p)) != 0; p++)
        {
            if ((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC))
            {
                if (ReadByte(++p) == 0)
                    break;
            }
            else if (b == '\\' || b == '/' || b == ':')
                name = p + 1;
        }
        if (name != path)
            MoveMemory(path, name, StringLength(name) + 1);
        return 0;
    }
    if (is("kernel32", "MultiByteToWideChar"))
    {
        // Aneiro's START turns its UTF-8 .wav.sli files into Shift-JIS through UTF-16: code page,
        // flags, string, bytes (-1: up to its 0, which counts), out, out size
        int length = Arg(3);
        if (length < 0)
            for (length = 0; ReadByte(Arg(2) + length) != 0; length++) {}
        std::u16string text = DecodeCodePage(Arg(0), ReadBytes(Arg(2), length));
        if (Arg(3) < 0)
            text += u'\0';
        if (Arg(5) == 0)
            return int(text.size());
        if (Arg(5) < int(text.size()))
            return 0;
        for (size_t k = 0; k < text.size(); k++)
            Write16(Arg(4) + 2 * int(k), text[k]);
        return int(text.size());
    }
    if (is("kernel32", "WideCharToMultiByte"))
    {
        // code page, flags, string, characters (-1: up to its 0), out, out size, default
        // character, whether it was used
        int length = Arg(3);
        if (length < 0)
            for (length = 0; Read16(Arg(2) + 2 * length) != 0; length++) {}
        std::u16string chars;
        for (int k = 0; k < length; k++)
            chars += char16_t(Read16(Arg(2) + 2 * k));
        std::string bytes = EncodeCodePage(Arg(0), chars);
        if (Arg(3) < 0)
            bytes += '\0';
        if (Arg(5) == 0)
            return int(bytes.size());
        if (Arg(5) < int(bytes.size()))
            return 0;
        WriteBytes(Arg(4), reinterpret_cast<const uint8_t*>(bytes.data()), int(bytes.size()));
        if (Arg(7) != 0)
            Write32(Arg(7), 0);
        return int(bytes.size());
    }
    return std::nullopt;
}

void ScnVm::Write64(int address, int64_t value)
{
    Write32(address, int(value));
    Write32(address + 4, int(value >> 32));
}

int ScnVm::NewGdiObject(const GdiObject& o)
{
    int handle = m_nextGdiObject++;
    m_gdiObjects[handle] = o;
    return handle;
}

bool ScnVm::GdiEllipse(int dc, int l, int t, int r, int b)
{
    // Ellipse on the surface behind a device context, with its brush and pen
    int surface = dc - 0x00030000;
    if (surface < 0 || surface >= SurfaceCount || SurfaceField(surface, 1) != dc || SurfaceField(surface, 2) == 0)
        return false;
    auto selected = m_gdiSelected.find(dc);
    auto [brushHandle, penHandle] = selected != m_gdiSelected.end() ? selected->second : std::pair(WhiteBrush, BlackPen);
    auto brushFound = m_gdiObjects.find(brushHandle), penFound = m_gdiObjects.find(penHandle);
    GdiObject brush = brushFound != m_gdiObjects.end() ? brushFound->second : m_gdiObjects[WhiteBrush];
    GdiObject pen = penFound != m_gdiObjects.end() ? penFound->second : m_gdiObjects[BlackPen];
    std::optional<int> fill = brush.Style == 1 ? std::nullopt : std::optional<int>(brush.Color);
    std::optional<ScnPen> outline = pen.Style == 5 ? std::nullopt : std::optional<ScnPen>(ScnPen{pen.Style, pen.Width, pen.Color});

    // Only the part of the surface the shape can reach goes to the platform
    int w = SurfaceField(surface, 7), h = SurfaceField(surface, 8);
    int pitch = SurfaceField(surface, 10), bytes = SurfaceField(surface, 9) >> 3, pixels = SurfaceField(surface, 2);
    if (bytes != 3 && bytes != 4)
        return false;
    IScnShapes* shapes = m_host.Shapes();
    if (!shapes)
        throw std::runtime_error("Ellipse without the platform's shapes (DibShapes) is not supported yet");
    int margin = (outline ? outline->Width : 0) + 1;
    int x0 = std::max(0, std::min(l, r) - margin), y0 = std::max(0, std::min(t, b) - margin);
    int x1 = std::min(w, std::max(l, r) + margin), y1 = std::min(h, std::max(t, b) + margin);
    if (x1 <= x0 || y1 <= y0)
        return true;
    int stride = (x1 - x0) * bytes;
    openshiina::Bytes region(static_cast<size_t>(stride) * size_t(y1 - y0));
    for (int y = y0; y < y1; y++)
        ReadBytes(pixels + y * pitch + x0 * bytes, region.data() + size_t(y - y0) * size_t(stride), stride);
    shapes->Ellipse(region.data(), x1 - x0, y1 - y0, stride, bytes, l - x0, t - y0, r - x0, b - y0, fill, outline);
    for (int y = y0; y < y1; y++)
        WriteBytes(pixels + y * pitch + x0 * bytes, region.data() + size_t(y - y0) * size_t(stride), stride);
    Shown(surface);
    return true;
}

} // namespace openshiina::scripting
