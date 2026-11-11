// C# Scripting/ScnVm.System.cs (and ScnVm.Diagnostics.cs' DescribeAddress): system opcodes of
// START's start-up - the engine version, the window size and colour depth, DirectDraw /
// DirectSound, message boxes, memory blocks, and the registry key the installer wrote (DataPath =
// where the save data lives, InstMode). The registry is emulated: DataPath is the host's save
// folder, so the scripts' save files never go to the game folder.
#include "scripting/ScnVm.h"

#include <format>
#include <map>
#include <regex>

#include "archives/FormatManager.h"
#include "text/Text.h"

namespace openshiina::scripting {

std::string ScnVm::DescribeAddress(int address) const
{
    for (size_t i = m_moduleImages.size(); i-- > 0;)
    {
        const auto& image = m_moduleImages[i];
        if (address >= image.Base && address < image.Base + int(image.Original.size()))
            return std::format("{}+{:05X}", Text::Cp932ToUtf8(image.File), address - image.Base);
    }
    return std::format("{:08X}", uint32_t(address));
}

std::optional<std::string> ScnVm::RegistryString(const std::string& name)
{
    std::string lower = name;
    for (char& ch : lower)
        if (ch >= 'A' && ch <= 'Z')
            ch = char(ch + 32);
    if (lower == "datapath")
        return DataPath();
    // the folder the game was installed in: the game folder, which the player's file names are
    // relative to
    if (lower == "instpath")
        return std::string(".\\");
    return std::nullopt;
}

std::optional<int> ScnVm::RegistryNumber(const std::string& name)
{
    std::string lower = name;
    for (char& ch : lower)
        if (ch >= 'A' && ch <= 'Z')
            ch = char(ch + 32);
    if (lower == "instmode")
        return InstallMode();
    return std::nullopt;
}

int ScnVm::InstallMode()
{
    if (!m_installMode)
        m_installMode = FindInstallMode();
    return *m_installMode;
}

// InstMode as the installer wrote it: the last install choice of SETUP.INI (InstallFiles0,
// InstallFiles1, ...) whose files beyond the earlier choices' are all in the game's folder; 0
// without one
int ScnVm::FindInstallMode()
{
    openshiina::Bytes bytes;
    if (!m_host.ReadLooseFile("SETUP.INI", bytes))
        return 0;
    std::string text = Text::Cp932ToUtf8(bytes.data(), bytes.size());
    std::map<int, std::vector<std::string>> choices;
    static const std::regex line(R"((?:^|\n)InstallFiles(\d+)=([^\n]*))", std::regex::icase);
    for (auto m = std::sregex_iterator(text.begin(), text.end(), line); m != std::sregex_iterator(); ++m)
    {
        std::string value = (*m)[2].str();
        if (!value.empty() && value.back() == '\r')
            value.pop_back();
        std::vector<std::string> files;
        size_t start = 0;
        while (start <= value.size())
        {
            size_t comma = value.find(',', start);
            std::string file = value.substr(start, comma == std::string::npos ? std::string::npos : comma - start);
            size_t a = file.find_first_not_of(" \t"), b = file.find_last_not_of(" \t\r");
            if (a != std::string::npos)
                files.push_back(file.substr(a, b - a + 1));
            if (comma == std::string::npos)
                break;
            start = comma + 1;
        }
        choices[std::stoi((*m)[1].str())] = files;
    }
    // A choice counts by the files it adds to the ones before it: a copied game may lack the others
    int best = 0;
    std::vector<std::string> before;
    auto seen = [&](const std::string& f) {
        for (const auto& b : before)
            if (Text::EqualsIgnoreCase(b, f))
                return true;
        return false;
    };
    for (const auto& [choice, files] : choices)
    {
        std::vector<std::string> added;
        for (const auto& f : files)
            if (!seen(f))
                added.push_back(f);
        bool all = true;
        for (const auto& f : added)
            all = all && m_host.LooseFileSize(Text::Utf8ToCp932(f)).has_value();
        if (choice > best && !added.empty() && all)
            best = choice;
        before.insert(before.end(), files.begin(), files.end());
    }
    return best;
}

std::string ScnVm::DataPath()
{
    std::string folder = m_host.SaveFolder();
    while (!folder.empty() && (folder.back() == '\\' || folder.back() == '/'))
        folder.pop_back();
    return Text::Utf8ToCp932(folder) + "\\";
}

std::optional<std::string> ScnVm::ExeFileName()
{
    auto* formats = archives::FormatManager::Instance();
    if (!formats)
        return std::nullopt;
    std::vector<std::string> names;
    for (const auto& [exe, game] : formats->GameMap().Items())
        if (game == Game)
            names.push_back(Text::Utf8ToCp932(exe));
    for (const auto& name : names)
        if (m_host.LooseFileSize(name))
            return name;
    if (!names.empty())
        return names.front();
    return std::nullopt;
}

namespace {

// 03C2's checksums of the unchanged executables, by game (see the C# player)
std::optional<int> ExeChecksum(const std::string& game)
{
    static const std::vector<std::pair<std::string, int>> checksums = {
        {"Azu Plus", 306723180 ^ 1804523910},
        {"Chuuchuu Nurse", int(0xF63C9C2D)},
        {"Can Fes! ~Itazura Majo to Naisho no Gakuensai~", 306723180 ^ -1948973168},
        {"Hana to Otome ni Shukufuku o Royal Bouquet", 306723180 ^ 1872894084},
        {"Saimin Seikatsu", 306723180 ^ -628140931},
        {"Doushite Daite Kurenai no!?", 306723180 ^ -1421357033},
        {"Nakadashi Trilogy", 306723180 ^ 228471671},
        {"Imouto Smile", 306723180 ^ 969238634},
        {"Yokorenbo ~Immoral Mother~", 306723180 ^ -1396214426},
        {"Hana to Otome ni Shukufuku o", 306723180 ^ 222200098},
        {"Engage Links", 306723180 ^ 1224645659},
        {"Chikan Densha Otoko 2", 306723180 ^ 741346554},
        {"Chikan Densha Otoko Gaiden", 306723180 ^ -1138810104},
        {"Tsuyoimo x Yowaimo", 306723180 ^ -770208518},
        {"Mahou Shoujo no Taisetsu na Koto", 306723180 ^ -1545457906},
        {"Shokuinshitsu", 306723180 ^ -661847700},
    };
    for (const auto& [name, value] : checksums)
        if (Text::EqualsIgnoreCase(name, game))
            return value;
    return std::nullopt;
}

} // namespace

void ScnVm::RegisterSystem()
{
    // 03C0 v, w: engine version (247) and 0x487F24
    Register(0x03C0, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.EngineVersion);
        vm.Store(c, i.Args[1], vm.EngineGlobal(0x487F24));
        return 0;
    });
    // 03C2 v: 0x13B41BC, a checksum of the executable made at start. START stops with "Program
    // Revision Error" unless v ^ 306723180 is the value of the unchanged executable: OpenShiina
    // gives that value, Azu Plus's for a game not listed
    Register(0x03C2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], ExeChecksum(vm.Game).value_or(*ExeChecksum("Azu Plus")));
        return 0;
    });
    // 09F6 w, h: the window size
    Register(0x09F6, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Store(c, i.Args[0], vm.ScreenWidth);
        vm.Store(c, i.Args[1], vm.ScreenHeight);
        return 0;
    });
    // 09E2 hdc, index, v: GetDeviceCaps of a true colour screen
    Register(0x09E2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.Value(c, i.Args[0]);
        int index = vm.Value(c, i.Args[1]);
        int value = index == 8 ? 1920 : index == 10 ? 1080 : index == 12 ? 32 : index == 14 ? 1 : index == 88 || index == 90 ? 96 : index == 116 ? 60 : 0;
        vm.Store(c, i.Args[2], value);
        return 0;
    });
    // 06C2 v / 06A4 v: DirectDraw / DirectSound started (non-zero = ready)
    Register(0x06C2, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], 1); return 0; });
    Register(0x06A4, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], 1); return 0; });
    // 06BA flags / 06BB v: DirectSound flags (0x13B52F0)
    Register(0x06BA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0x13B52F0, c, i); });
    Register(0x06BB, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.EngineGlobal(0x13B52F0)); return 0; });
    // 049D v / 049C v: set / get the engine's flag at 0xD0AD3C (Tanetsuke Mura's START sets it from
    // RIO.INI's MouseLR_Change)
    Register(0x049D, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { return vm.SetGlobal(0xD0AD3C, c, i); });
    Register(0x049C, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.EngineGlobal(0xD0AD3C)); return 0; });
    // 07E8 v: v = RIO.INI's Background (0x13B43D0), as 07E5 / 07E6 last set it
    Register(0x07E8, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], vm.RunsInBackground() ? 1 : 0); return 0; });
    // 073F v: the COM objects for movies were created
    Register(0x073F, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) { vm.Store(c, i.Args[0], 1); return 0; });
    // 0780 text, caption, type, v: MessageBox
    Register(0x0780, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string text = vm.ReadString(vm.Value(c, i.Args[0]));
        std::string caption = vm.ReadString(vm.Value(c, i.Args[1]));
        int type = vm.Value(c, i.Args[2]);
        vm.Store(c, i.Args[3], vm.m_host.MessageBox(text, caption, type));
        return 0;
    });

    // 02BC n, v: GlobalAlloc (zeroed); 02BD p: GlobalFree. The block's size is kept like a loaded
    // file's (GlobalSize)
    Register(0x02BC, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        int size = vm.Value(c, i.Args[0]);
        int block = vm.Allocate(size);
        vm.m_fileSizes[block] = size;
        vm.Store(c, i.Args[1], block);
        return 0;
    });
    Register(0x02BD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.FreeScriptBlock(vm.Value(c, i.Args[0]));
        return 0;
    });

    // 00FA root, key: open a registry key (the copy-protection CRC of the exe is not checked)
    Register(0x00FA, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        vm.EngineGlobals[0x4880CC] = vm.Value(c, i.Args[0]);
        vm.m_registryKey = vm.ReadString(vm.Value(c, i.Args[1]));
        return 0;
    });
    // 00FF name, v: whether the value exists
    Register(0x00FF, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string name = vm.ReadString(vm.Value(c, i.Args[0]));
        vm.Store(c, i.Args[1], vm.RegistryString(name) || vm.RegistryNumber(name) ? 1 : 0);
        return 0;
    });
    // 00FE name, buffer: a string value
    Register(0x00FE, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string name = vm.ReadString(vm.Value(c, i.Args[0]));
        int buffer = vm.Value(c, i.Args[1]);
        auto text = vm.RegistryString(name);
        if (!text)
            return 2;
        vm.WriteString(buffer, *text);
        return 0;
    });
    // 00FD name, v: a number value
    Register(0x00FD, [](ScnVm& vm, ScnContext& c, const ScnInstruction& i) {
        std::string name = vm.ReadString(vm.Value(c, i.Args[0]));
        auto number = vm.RegistryNumber(name);
        if (!number)
            return 2;
        vm.Store(c, i.Args[1], *number);
        return 0;
    });
}

} // namespace openshiina::scripting
