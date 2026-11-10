// scnrun: a game's scripts run on the C++ interpreter as the C# player's test runner (tests/
// OpenShiina.ScnBoot) runs them, to compare the two: the clock moves 1/60 s a frame, the save data
// goes to the same temporary folder (so that DataPath, which the scripts keep in their memory, is
// the same), no input. With from:to, every instruction run in those frames is listed as ScnBoot's
// SCNBOOT_HOT lists it ("  [hot] slot 0 start.scn+00000 001E x1"). SCNRUN_PICTURES=folder saves the
// window's picture as frame_NNNN.png every SCNRUN_EVERY frames (default 10) and when the run ends, as
// ScnBoot's picture folder does (screencompare.py compares the two folders pixel for pixel).
//
//   scnrun <the C# player's Data folder> <game folder> <frames> [from:to]
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <format>
#include <map>
#include <regex>
#include <string>
#include <vector>

#include <miniz.h>

#include "archives/FormatManager.h"
#include "GdiFonts.h"
#include "GdiShapes.h"
#include "game/GameData.h"
#include "platform/Files.h"
#include "scripting/ScnVm.h"
#include "text/Text.h"

// Last: it makes macros of names the interpreter has (FillMemory, MessageBox...)
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#undef MessageBox
#endif

using namespace openshiina;
using namespace openshiina::scripting;

namespace {

std::string Utf8(const std::string& sjis) { return Text::Cp932ToUtf8(sjis); }

std::string Slashes(std::string name)
{
    for (char& c : name)
        if (c == '/')
            c = '\\';
    return name;
}

/// PlayerFolders.FolderName: the characters a file name cannot hold become '_'.
std::string FolderName(const std::string& game)
{
    std::string name = game;
    for (char& c : name)
        if (uint8_t(c) < 32 || std::string_view("\"<>|:*?\\/").find(c) != std::string_view::npos)
            c = '_';
    return name;
}

// Music that only says what the scripts do with it, as ScnBoot's TestMusic (without decoding)
class TestMusic : public IScnMusic
{
public:
    int Open(const Bytes& file) override
    {
        std::printf("  [music] %zu bytes\n", file.size());
        return m_next++;
    }

    void Close(int stream) override { std::printf("  [music] close %d\n", stream); }
    void Play(int stream, bool loop) override { std::printf("  [music] play %d%s\n", stream, loop ? " looping" : ""); }
    void Stop(int) override {}
    void Pause(int stream) override { std::printf("  [music] pause %d\n", stream); }
    void Resume(int stream) override { std::printf("  [music] resume %d\n", stream); }
    void SetVolume(int stream, int volume) override { std::printf("  [music] volume %d %d\n", stream, volume); }
    bool IsPlaying(int) override { return true; }

private:
    int m_next = 1;
};

/// A comma- or semicolon-separated environment variable's items.
std::vector<std::string> Items(const char* variable, char separator)
{
    std::vector<std::string> items;
    const char* value = std::getenv(variable);
    std::string text = value ? value : "";
    for (size_t start = 0; start < text.size();)
    {
        size_t end = text.find(separator, start);
        if (end == std::string::npos)
            end = text.size();
        if (end > start)
            items.push_back(text.substr(start, end - start));
        start = end + 1;
    }
    return items;
}

class Host : public IScnHost
{
public:
    Host(game::GameData& data, std::string saveFolder) : m_data(data), m_saveFolder(std::move(saveFolder))
    {
        // SCNRUN_PRESS (ScnBoot's SCNBOOT_PRESS): "frame:key[:frames],..." keys held from that frame
        for (const std::string& item : Items("SCNRUN_PRESS", ','))
        {
            size_t a = item.find(':'), b = item.find(':', a + 1);
            if (a == std::string::npos)
                continue;
            int from = std::stoi(item.substr(0, a));
            std::string key = item.substr(a + 1, b == std::string::npos ? std::string::npos : b - a - 1);
            int frames = b == std::string::npos ? 3 : std::stoi(item.substr(b + 1));
            int code = key.rfind("0x", 0) == 0 || key.rfind("0X", 0) == 0 ? std::stoi(key.substr(2), nullptr, 16) : std::stoi(key);
            m_presses.push_back({from, from + frames - 1, code});
        }
        // SCNRUN_FILE (SCNBOOT_FILE): "name=file;..." a file of the archives read from a file of the
        // disk instead (a test scenario in place of one of the game's); the game folder is not touched
        for (const std::string& item : Items("SCNRUN_FILE", ';'))
            if (size_t eq = item.find('='); eq != std::string::npos)
                m_swaps.emplace_back(item.substr(0, eq), item.substr(eq + 1));
        // SCNRUN_MOUSE (SCNBOOT_MOUSE): "frame:x,y[:buttons[:frames]];..." the mouse there from that
        // frame, the buttons held so many frames
        for (const std::string& item : Items("SCNRUN_MOUSE", ';'))
        {
            size_t a = item.find(':'), comma = item.find(',', a + 1), b = item.find(':', a + 1), c = b == std::string::npos ? b : item.find(':', b + 1);
            if (a == std::string::npos || comma == std::string::npos)
                continue;
            Click m{};
            m.From = std::stoi(item.substr(0, a));
            m.X = std::stoi(item.substr(a + 1, comma - a - 1));
            m.Y = std::stoi(item.substr(comma + 1, b == std::string::npos ? std::string::npos : b - comma - 1));
            m.Buttons = b == std::string::npos ? 0 : std::stoi(item.substr(b + 1, c == std::string::npos ? std::string::npos : c - b - 1));
            m.To = m.From + (c == std::string::npos ? 3 : std::stoi(item.substr(c + 1))) - 1;
            m_mouse.push_back(m);
        }
        std::stable_sort(m_mouse.begin(), m_mouse.end(), [](const Click& p, const Click& q) { return p.From < q.From; });
    }

    std::pair<int, int> MousePosition() override
    {
        std::pair<int, int> at{0, 0};
        for (const Click& m : m_mouse)
            if (m.From <= Frame)
                at = {m.X, m.Y};
        return at;
    }

    int MouseButtons() override
    {
        for (const Click& m : m_mouse)
            if (m.From <= Frame && Frame <= m.To)
                return m.Buttons;
        return 0;
    }

    uint32_t Clock = 0;
    int Frame = 0;

    bool KeyDown(int key) override
    {
        for (const auto& p : m_presses)
            if (p.Key == key && Frame >= p.From && Frame <= p.To)
                return true;
        return false;
    }

    /// The window messages of this frame: the keys of SCNRUN_PRESS that go down or up.
    std::vector<std::pair<int, int>> Messages() const
    {
        std::vector<std::pair<int, int>> messages;
        for (const auto& p : m_presses)
        {
            if (p.From == Frame)
                messages.emplace_back(ScnMessage::KeyDown, p.Key);
            if (p.To + 1 == Frame)
                messages.emplace_back(ScnMessage::KeyUp, p.Key);
        }
        const std::tuple<int, int, int> buttons[] = {{1, ScnMessage::LButtonDown, ScnMessage::LButtonUp},
                                                     {2, ScnMessage::RButtonDown, ScnMessage::RButtonUp},
                                                     {4, ScnMessage::MButtonDown, ScnMessage::MButtonUp}};
        for (const Click& m : m_mouse)
            for (const auto& [bit, down, up] : buttons)
            {
                if ((m.Buttons & bit) == 0)
                    continue;
                if (m.From == Frame)
                    messages.emplace_back(down, bit);
                if (m.To + 1 == Frame)
                    messages.emplace_back(up, 0);
            }
        return messages;
    }

    std::string SaveFolder() override { return m_saveFolder; }

    bool ReadSaveFile(const std::string& name, Bytes& data) override
    {
        return Files::ReadAll(Files::Combine(m_saveFolder, Utf8(name)), data);
    }

    void WriteSaveFile(const std::string& name, const Bytes& bytes) override
    {
        std::printf("  [write] %s (%zu bytes)\n", Utf8(name).c_str(), bytes.size());
        if (std::FILE* f = Files::Open(Files::Combine(m_saveFolder, Utf8(name)), "wb"))
        {
            std::fwrite(bytes.data(), 1, bytes.size(), f);
            std::fclose(f);
        }
    }

    void DeleteSaveFile(const std::string& name) override
    {
        std::error_code error;
        std::filesystem::remove(Files::ToPath(Files::Combine(m_saveFolder, Utf8(name))), error);
    }

    int MessageBox(const std::string& text, const std::string&, int type) override
    {
        std::printf("  [message box %d] %s\n", type, Utf8(text).c_str());
        return (type & 0xF) == 4 || (type & 0xF) == 3 ? 6 : 1;    // yes / OK
    }

    bool ReadFile(const std::string& name, Bytes& data) override
    {
        if (auto swap = Swap(name))
            return Files::ReadAll(*swap, data);
        return m_data.Read(Slashes(Utf8(name)), data);
    }
    std::optional<int64_t> ArchiveFileSize(const std::string& name) override
    {
        if (auto swap = Swap(name))
        {
            Bytes data;
            return Files::ReadAll(*swap, data) ? std::optional<int64_t>(int64_t(data.size())) : std::nullopt;
        }
        return m_data.Size(Slashes(Utf8(name)));
    }
    std::optional<int64_t> ArchiveFileSize(const std::string& archive, const std::string& name) override
    {
        return m_data.Size(Utf8(archive), Slashes(Utf8(name)));
    }
    bool MountArchive(const std::string& archive, bool mounted) override { return m_data.Mount(Utf8(archive), mounted); }
    bool ExecutableCopy(const std::string& name, Bytes& data) override { return m_data.ExecutableCopy(LooseFileSize(name), data); }

    std::optional<int64_t> LooseFileSize(const std::string& name) override
    {
        Bytes bytes;
        if (Text::EqualsIgnoreCase(Utf8(name), "RIO.INI"))
            return ReadLooseFile(name, bytes) ? std::optional<int64_t>(int64_t(bytes.size())) : std::nullopt;
        auto path = m_data.LooseFile(Utf8(name));
        if (!path)
            return std::nullopt;
        std::error_code error;
        auto size = std::filesystem::file_size(Files::ToPath(*path), error);
        return error ? std::nullopt : std::optional<int64_t>(int64_t(size));
    }

    bool ReadLooseFile(const std::string& name, Bytes& data) override
    {
        auto path = m_data.LooseFile(Utf8(name));
        return path && Files::ReadAll(*path, data);
    }

    uint32_t Milliseconds() override { return Clock; }
    scripting::IScnFonts* Fonts() override { return &m_fonts; }
    // GDI's own ellipses, as ScnBoot draws them on Windows (the Switch draws with DibShapes)
    scripting::IScnShapes* Shapes() override { return &m_shapes; }
    IScnMusic* Music() override { return &m_music; }
    void SetTitle(const std::string& title) override { std::printf("  [title] %s\n", Utf8(title).c_str()); }
    void SetFullScreen(bool) override {}

private:
    struct Press
    {
        int From, To, Key;
    };
    std::vector<Press> m_presses;
    struct Click
    {
        int From, X, Y, Buttons, To;
    };
    std::vector<Click> m_mouse;
    std::vector<std::pair<std::string, std::string>> m_swaps;

    std::optional<std::string> Swap(const std::string& name) const
    {
        std::string utf8 = Utf8(name);
        for (const auto& [end, file] : m_swaps)
            if (utf8.size() >= end.size() && Text::EqualsIgnoreCase(utf8.substr(utf8.size() - end.size()), end))
                return file;
        return std::nullopt;
    }

    game::GameData& m_data;
    std::string m_saveFolder;
    tools::GdiFonts m_fonts;
    tools::GdiShapes m_shapes;
    TestMusic m_music;
};

/// The window's picture (BGR rows) as frame_NNNN.png in folder.
void SaveScreen(ScnVm& vm, const std::string& folder, int frame)
{
    const Bytes& window = vm.Window();
    int w = vm.ScreenWidth, h = vm.ScreenHeight;
    Bytes rgb(size_t(w) * size_t(h) * 3);
    for (size_t i = 0; i + 2 < rgb.size() && i + 2 < window.size(); i += 3)
    {
        rgb[i] = window[i + 2];
        rgb[i + 1] = window[i + 1];
        rgb[i + 2] = window[i];
    }
    size_t length = 0;
    void* png = tdefl_write_image_to_png_file_in_memory_ex(rgb.data(), w, h, 3, &length, 6, MZ_FALSE);
    if (!png)
        return;
    if (std::FILE* f = Files::Open(Files::Combine(folder, std::format("frame_{:04}.png", frame)), "wb"))
    {
        std::fwrite(png, 1, length, f);
        std::fclose(f);
    }
    mz_free(png);
}

std::string Match(const std::string& text, const char* pattern)
{
    std::smatch m;
    std::regex re(pattern, std::regex::icase);
    return std::regex_search(text, m, re) ? m[1].str() : std::string();
}

std::string Trim(const std::string& text)
{
    size_t a = text.find_first_not_of(" \t\r"), b = text.find_last_not_of(" \t\r");
    return a == std::string::npos ? std::string() : text.substr(a, b - a + 1);
}

} // namespace

int Run(int argc, std::string* argv)
{
    if (argc < 4)
    {
        std::fprintf(stderr, "scnrun <Data folder> <game folder> <frames> [from:to]\n");
        return 2;
    }
    std::string dataFolder = argv[1], folder = argv[2];
    int frames = std::stoi(argv[3]);
    int hotFrom = 0x7FFFFFFF, hotTo = -1;
    if (argc > 4)
    {
        const std::string& range = argv[4];
        size_t colon = range.find(':');
        hotFrom = std::stoi(range.substr(0, colon));
        hotTo = colon == std::string::npos ? hotFrom : std::stoi(range.substr(colon + 1));
    }
    std::setvbuf(stdout, nullptr, _IOFBF, 1 << 16);

    archives::FormatManager::SetInstance(std::make_unique<archives::FormatManager>(Files::Combine(dataFolder, "Formats.Json")));
    // RIO.INI: engine version and the first script
    std::string ini;
    if (auto iniPath = game::GameData::EngineIni(folder))
    {
        Bytes bytes;
        if (Files::ReadAll(*iniPath, bytes))
            ini = Text::Cp932ToUtf8(bytes.data(), bytes.size());
    }
    std::string version = Match(ini, R"(v(\d+\.\d+))");
    std::string start = Trim(Match(ini, R"((?:^|\n)Scn=([^\n]+))"));
    auto archive = game::GameData::FindArchive(folder);
    auto scheme = archive ? archives::FormatManager::Instance()->SchemeForArchive(*archive) : nullptr;
    if (!scheme)
    {
        std::printf("The game was not recognised.\n");
        return 1;
    }
    auto data = game::GameData::Open(folder, scheme);
    std::printf("%s, engine v%s, first script %s\n", scheme->Name.c_str(), version.c_str(), start.c_str());

    // Save data of the test run: ScnBoot's temporary folder, never the player's real data
    std::string saves = Files::Combine(Files::Combine(Files::ToUtf8(std::filesystem::temp_directory_path()), "openshiina-scnboot"),
                                       FolderName(scheme->Name));
    Files::CreateDirectories(saves);
    Host host(*data, saves);
    const auto& opcodes = ScnOpcodes::ForVersion(Files::Combine(dataFolder, "ScnOps"), version);
    ScnVm vm(opcodes, host);
    vm.Game = scheme->Name;
    vm.EngineVersion = int(std::lround(std::stod(version) * 100));
    if (auto w = Match(ini, R"((?:^|\n)WindowWidth=(\d+))"); !w.empty())
        vm.ScreenWidth = std::stoi(w);
    if (auto h = Match(ini, R"((?:^|\n)WindowHeight=(\d+))"); !h.empty())
        vm.ScreenHeight = std::stoi(h);
    // As ScnBoot's SCNBOOT_BGLOAD / SCNBOOT_AHEAD: the background loader and the reads ahead
    const char* bgload = std::getenv("SCNRUN_BGLOAD");
    const char* ahead = std::getenv("SCNRUN_AHEAD");
    vm.LoadInBackground = bgload && std::string(bgload) == "1";
    vm.ReadAhead = !(ahead && std::string(ahead) == "0");
    // SCNRUN_TEXTCHECK=1: every glyph drawn in place is drawn again the old way and compared
    const char* textCheck = std::getenv("SCNRUN_TEXTCHECK");
    vm.TextCheck = textCheck && std::string(textCheck) == "1";
    // SCNRUN_TIMES=1: the time of each embedded routine and text step (the Switch player's slow log), at the end
    const char* timesVariable = std::getenv("SCNRUN_TIMES");
    std::unordered_map<std::string, ScnVm::RoutineTime> routineTimes;
    if (timesVariable && std::string(timesVariable) == "1")
        vm.RoutineTimes = &routineTimes;
    if (!vm.LoadModule(0, Text::Utf8ToCp932(start), true))
    {
        std::printf("%s is missing.\n", start.c_str());
        return 1;
    }

    const char* picturesVariable = std::getenv("SCNRUN_PICTURES");
    std::string pictures = picturesVariable ? picturesVariable : "";
    const char* everyVariable = std::getenv("SCNRUN_EVERY");
    int every = everyVariable ? std::max(1, std::atoi(everyVariable)) : 10;
    if (!pictures.empty())
        Files::CreateDirectories(pictures);

    std::map<ScnVm::HotSpot, int> hot;
    int frame = 0;
    auto started = std::chrono::steady_clock::now();
    try
    {
        for (; frame < frames; frame++)
        {
            host.Clock = uint32_t(int64_t(frame) * 1000 / 60);
            host.Frame = frame;
            if (frame == hotFrom)
                vm.HotSpots = &hot;
            if (frame == hotTo + 1 && vm.HotSpots)
            {
                vm.HotSpots = nullptr;
                for (const auto& [spot, times] : hot)
                {
                    auto [slot, codeBase, offset, op] = spot;
                    std::printf("  [hot] slot %d %s %04X x%d\n", slot, vm.DescribeAddress(codeBase + offset).c_str(), op, times);
                }
            }
            for (const auto& [message, wParam] : host.Messages())
            {
                bool mouse = message >= ScnMessage::LButtonDown && message <= ScnMessage::MButtonUp;
                auto [mx, my] = host.MousePosition();
                vm.WindowMessage(message, wParam, mouse ? ScnMessage::Point(mx, my) : ScnMessage::Key(0, message == ScnMessage::KeyDown, false));
            }
            if (!vm.RunFrame())
            {
                std::printf("The scripts quit after %d frames.\n", frame);
                break;
            }
            if (!pictures.empty() && frame % every == 0)
                SaveScreen(vm, pictures, frame);
        }
        if (frame == frames)
            std::printf("Still running after %d frames.\n", frames);
        if (!pictures.empty())
            SaveScreen(vm, pictures, frame);
    }
    catch (const ScnException& e)
    {
        std::printf("Script error at frame %d: %s\n  at %s, opcode %04X\n", frame, e.what(), vm.DescribeAddress(e.Address).c_str(), e.Op);
        if (vm.HotSpots)
            for (const auto& [spot, times] : hot)
            {
                auto [slot, codeBase, offset, op] = spot;
                std::printf("  [hot so far] slot %d %s %04X x%d\n", slot, vm.DescribeAddress(codeBase + offset).c_str(), op, times);
            }
        return 1;
    }
    catch (const std::exception& e)
    {
        std::printf("Error at frame %d: %s\n", frame, e.what());
        return 1;
    }
    double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    std::printf("%d frames in %.2f s, script memory %lld MB\n", frame, seconds, (long long)(vm.ScriptMemoryBytes() >> 20));
    if (vm.LoadInBackground)
        std::printf("  read ahead: %d files, %d of them used\n", vm.ReadAheadCounts().first, vm.ReadAheadCounts().second);
    if (vm.RoutineTimes)
    {
        std::vector<std::pair<std::string, ScnVm::RoutineTime>> times(routineTimes.begin(), routineTimes.end());
        std::sort(times.begin(), times.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
        for (size_t k = 0; k < times.size() && k < 12; k++)
            std::printf("  [time] %s %.1f ms (%d calls)\n", times[k].first.c_str(), times[k].second.Milliseconds, times[k].second.Calls);
    }
    if (vm.TextCheck)
    {
        std::printf("  text check: %lld glyphs, %lld differed\n", (long long)vm.TextCheckGlyphs, (long long)vm.TextCheckDiffered);
        for (const auto& check : vm.TextChecks)
            std::printf("    %s\n", check.c_str());
    }
    // As ScnBoot ends its output: each opcode run, how often
    std::string counts;
    int kinds = 0;
    for (size_t op = 0; op < vm.OpCounts.size(); op++)
        if (vm.OpCounts[op] != 0)
        {
            counts += std::format(" {:04X}x{}", op, vm.OpCounts[op]);
            kinds++;
        }
    std::printf("Opcodes run (%d kinds):%s\n", kinds, counts.c_str());
    return 0;
}

#ifdef _WIN32
#include <dbghelp.h>
#pragma comment(lib, "dbghelp.lib")

// SCNRUN_BIG=<MB>: every allocation of that much or more is printed with where it was made (the
// Switch's applet mode has little memory: what takes it)
static size_t BigAllocation()
{
    static const size_t big = [] {
        const char* v = std::getenv("SCNRUN_BIG");
        return v ? size_t(std::atoi(v)) << 20 : size_t(0);
    }();
    return big;
}

void* operator new(size_t size)
{
    static thread_local bool inside = false;
    if (size_t big = BigAllocation(); big != 0 && size >= big && !inside)
    {
        inside = true;
        void* frames[24];
        USHORT n = CaptureStackBackTrace(1, 24, frames, nullptr);
        HANDLE process = GetCurrentProcess();
        static bool symbols = SymInitialize(process, nullptr, TRUE);
        std::printf("  [big] %zu bytes\n", size);
        alignas(SYMBOL_INFO) char buffer[sizeof(SYMBOL_INFO) + 256];
        auto* symbol = reinterpret_cast<SYMBOL_INFO*>(buffer);
        for (USHORT k = 0; k < n && symbols; k++)
        {
            symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
            symbol->MaxNameLen = 255;
            if (SymFromAddr(process, DWORD64(frames[k]), nullptr, symbol))
                std::printf("    %s\n", symbol->Name);
        }
        inside = false;
    }
    if (void* p = std::malloc(size ? size : 1))
        return p;
    throw std::bad_alloc();
}

void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
#endif

// The arguments as UTF-8: on Windows from the UTF-16 command line (main's are in the ANSI code
// page, which has no Japanese on most PCs)
#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
    SetConsoleOutputCP(CP_UTF8);
    std::vector<std::string> args;
    for (int i = 0; i < argc; i++)
        args.push_back(Files::ToUtf8(std::filesystem::path(argv[i])));
    return Run(argc, args.data());
}
#else
int main(int argc, char** argv)
{
    std::vector<std::string> args(argv, argv + argc);
    return Run(argc, args.data());
}
#endif
