// routinecheck: the signatures of a game's x86 routines as the C++ interpreter finds them, against
// the list the C# player's tools make of them ("ScnTools routines <game folder>"): each line of
// that list ("  START.SCN 6A534  B6FF...  56 instr calls  C# cpuid") is the routine at that
// offset of that module, found again here and compared (signature, instructions, calls, loops).
//
//   routinecheck <the C# player's Data folder> <game folder> <ScnTools routines output>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <regex>
#include <string>
#include <vector>

#include "archives/FormatManager.h"
#include "game/GameData.h"
#include "platform/Files.h"
#include "scripting/ScnVm.h"
#include "scripting/X86Routine.h"
#include "text/Text.h"

using namespace openshiina;
using namespace openshiina::scripting;

namespace {

class NoHost : public IScnHost
{
public:
    bool ReadFile(const std::string&, Bytes&) override { return false; }
    bool ReadLooseFile(const std::string&, Bytes&) override { return false; }
    uint32_t Milliseconds() override { return 0; }
    void SetTitle(const std::string&) override {}
    void SetFullScreen(bool) override {}
    std::string SaveFolder() override { return "."; }
    bool ReadSaveFile(const std::string&, Bytes&) override { return false; }
    void WriteSaveFile(const std::string&, const Bytes&) override {}
    void DeleteSaveFile(const std::string&) override {}
    int MessageBox(const std::string&, const std::string&, int) override { return 1; }
};

int Run(int argc, std::string* argv)
{
    if (argc < 4)
    {
        std::fprintf(stderr, "routinecheck <Data folder> <game folder> <ScnTools routines output>\n");
        return 2;
    }
    archives::FormatManager::SetInstance(std::make_unique<archives::FormatManager>(Files::Combine(argv[1], "Formats.Json")));
    auto archive = game::GameData::FindArchive(argv[2]);
    auto scheme = archive ? archives::FormatManager::Instance()->SchemeForArchive(*archive) : nullptr;
    if (!scheme)
    {
        std::printf("%s: not recognised\n", argv[2].c_str());
        return 2;
    }
    auto data = game::GameData::Open(argv[2], scheme);
    Bytes list;
    if (!Files::ReadAll(argv[3], list))
    {
        std::printf("%s: cannot read\n", argv[3].c_str());
        return 2;
    }
    NoHost host;
    ScnVm vm(ScnOpcodes::ForVersion(Files::Combine(argv[1], "ScnOps"), "2.49"), host);
    std::map<std::string, int> modules;
    std::regex line(R"(^  (\S+) ([0-9A-F]{5})  ([0-9A-F]{40}) +(\d+) instr( calls)?( loops)?)");
    std::string text(list.begin(), list.end());
    int checked = 0, different = 0;
    size_t start = 0;
    while (start < text.size())
    {
        size_t end = text.find('\n', start);
        std::string l = text.substr(start, end == std::string::npos ? std::string::npos : end - start);
        start = end == std::string::npos ? text.size() : end + 1;
        if (!l.empty() && l.back() == '\r')
            l.pop_back();
        std::smatch m;
        if (!std::regex_search(l, m, line))
            continue;
        std::string module = m[1].str();
        auto known = modules.find(module);
        if (known == modules.end())
        {
            Bytes code;
            if (!data->Read(module, code))
            {
                std::printf("%s: not in the archives\n", module.c_str());
                different++;
                continue;
            }
            known = modules.emplace(module, vm.AllocateCopy(code)).first;
        }
        int offset = std::stoi(m[2].str(), nullptr, 16);
        X86Routine routine(vm, uint32_t(known->second + offset));
        bool calls = m[5].matched, loops = m[6].matched;
        int count = std::stoi(m[4].str());
        checked++;
        if (routine.Signature != m[3].str() || int(routine.Code.size()) != count || routine.Calls != calls || routine.Loops() != loops)
        {
            different++;
            std::printf("  %s %05X: %s %zu instr%s%s, the C# player's %s %d instr%s%s\n", module.c_str(), offset, routine.Signature.c_str(),
                        routine.Code.size(), routine.Calls ? " calls" : "", routine.Loops() ? " loops" : "", m[3].str().c_str(), count,
                        calls ? " calls" : "", loops ? " loops" : "");
        }
    }
    std::printf("%s: %d routines, %d different\n", scheme->Name.c_str(), checked, different);
    return different == 0 ? 0 : 1;
}

} // namespace

#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
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
