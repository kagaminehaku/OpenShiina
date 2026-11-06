// arcdump <Formats.Json> <game folder> [out.txt]: the lines of DumpGame for one game (on the
// standard output when no file is given).
#include <cstdio>
#include <memory>
#include <string>
#include <vector>

#include "ArcDump.h"
#include "archives/FormatManager.h"
#include "platform/Files.h"

using namespace openshiina;

static int Run(int argc, const std::string* argv)
{
    if (argc < 3 || argc > 4)
    {
        std::fprintf(stderr, "usage: arcdump <Formats.Json> <game folder> [out.txt]\n");
        return 1;
    }
    auto manager = std::make_unique<archives::FormatManager>(argv[1]);
    for (const auto& error : manager->LoadErrors())
        std::fprintf(stderr, "%s\n", error.c_str());
    archives::FormatManager::SetInstance(std::move(manager));

    std::FILE* out = argc == 4 ? Files::Open(argv[3], "wb") : stdout;
    if (!out)
    {
        std::fprintf(stderr, "cannot write %s\n", argv[3].c_str());
        return 1;
    }
    int bad = tools::DumpGame(argv[2], out, [](const std::string& archive) { std::fprintf(stderr, "%s\n", archive.c_str()); });
    if (out != stdout)
        std::fclose(out);
    if (bad < 0)
        std::fprintf(stderr, "not opened\n");
    else
        std::fprintf(stderr, "%d files not decoded\n", bad);
    return bad == 0 ? 0 : 2;
}

// The arguments as UTF-8: on Windows from the UTF-16 command line (main's are in the ANSI code
// page, which has no Japanese on most PCs)
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
