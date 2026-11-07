// scnrun: a game's scripts run on the C++ interpreter as the C# player's test runner (tests/
// OpenShiina.ScnBoot) runs them, to compare the two: the clock moves 1/60 s a frame, the save data
// goes to the same temporary folder (so that DataPath, which the scripts keep in their memory, is
// the same), no input. With from:to, every instruction run in those frames is listed as ScnBoot's
// SCNBOOT_HOT lists it ("  [hot] slot 0 start.scn+00000 001E x1").
//
//   scnrun <the C# player's Data folder> <game folder> <frames> [from:to]
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <format>
#include <map>
#include <regex>
#include <string>
#include <vector>

#include "archives/FormatManager.h"
#include "GdiFonts.h"
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

class Host : public IScnHost
{
public:
    Host(game::GameData& data, std::string saveFolder) : m_data(data), m_saveFolder(std::move(saveFolder)) {}

    uint32_t Clock = 0;
    int Frame = 0;

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

    bool ReadFile(const std::string& name, Bytes& data) override { return m_data.Read(Slashes(Utf8(name)), data); }
    std::optional<int64_t> ArchiveFileSize(const std::string& name) override { return m_data.Size(Slashes(Utf8(name))); }
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
    IScnMusic* Music() override { return &m_music; }
    void SetTitle(const std::string& title) override { std::printf("  [title] %s\n", Utf8(title).c_str()); }
    void SetFullScreen(bool) override {}

private:
    game::GameData& m_data;
    std::string m_saveFolder;
    tools::GdiFonts m_fonts;
    TestMusic m_music;
};

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
    if (!vm.LoadModule(0, Text::Utf8ToCp932(start), true))
    {
        std::printf("%s is missing.\n", start.c_str());
        return 1;
    }

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
            if (!vm.RunFrame())
            {
                std::printf("The scripts quit after %d frames.\n", frame);
                break;
            }
        }
        if (frame == frames)
            std::printf("Still running after %d frames.\n", frames);
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
    std::printf("%d frames in %.2f s\n", frame, seconds);
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
