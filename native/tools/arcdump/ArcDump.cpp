#include "ArcDump.h"

#include <algorithm>
#include <memory>

#include "archives/FormatManager.h"
#include "archives/WarcOpener.h"
#include "platform/Files.h"
#include "text/Text.h"
#include "util/Encoding.h"

namespace openshiina::tools {

using namespace openshiina::archives;

int DumpGame(const std::string& folder, std::FILE* out, const std::function<void(const std::string&)>& progress)
{
    // The game's .WAR archives by name, any case (GameData.FindArchive, ScnTools GameArchives)
    std::vector<std::string> wars;
    for (const auto& name : Files::FileNames(folder))
        if (Text::EqualsIgnoreCase(Files::Extension(name), ".war"))
            wars.push_back(name);
    std::sort(wars.begin(), wars.end(), [](const std::string& a, const std::string& b) { return Text::LessIgnoreCase(a, b); });
    if (wars.empty())
    {
        std::fprintf(out, "# %s: no .WAR archive in the folder\n", folder.c_str());
        return -1;
    }
    FormatManager* manager = FormatManager::Instance();
    auto scheme = manager ? manager->SchemeForArchive(Files::Combine(folder, wars.front())) : nullptr;
    if (!scheme)
    {
        std::fprintf(out, "# %s: no scheme in Formats.Json opens its archives\n", folder.c_str());
        return -1;
    }
    std::fprintf(out, "# scheme %s\n", scheme->Name.c_str());

    int bad = 0;
    for (const auto& war : wars)
    {
        progress(war);
        std::unique_ptr<WarcArchive> warc;
        try
        {
            warc = WarcOpener::TryOpen(std::make_unique<ArcView>(Files::Combine(folder, war)), scheme);
        }
        catch (const std::exception& ex)
        {
            std::fprintf(out, "# %s: %s\n", war.c_str(), ex.what());
        }
        if (!warc)
        {
            std::fprintf(out, "# %s: not opened\n", war.c_str());
            continue;
        }
        std::string stem = Files::FileNameWithoutExtension(war);
        for (const auto& entry : warc->Entries)
        {
            // As ScnTools extract names the file it writes
            std::string name = entry.Name;
            std::replace(name.begin(), name.end(), '\\', '_');
            std::replace(name.begin(), name.end(), '/', '_');
            try
            {
                Bytes data = WarcOpener::OpenEntry(*warc, entry);
                std::string hash = Encoding::Sha256::HexOf(data.data(), data.size());
                std::fprintf(out, "%s_%s\t%zu\t%08X\t%s\n", stem.c_str(), name.c_str(), data.size(), entry.Flags, hash.c_str());
            }
            catch (const std::exception& ex)
            {
                std::fprintf(out, "%s_%s\t-\t%08X\tERROR %s\n", stem.c_str(), name.c_str(), entry.Flags, ex.what());
                bad++;
            }
        }
    }
    return bad;
}

} // namespace openshiina::tools
