// The Switch player's settings (see Settings.h)
#include "Settings.h"

#include <algorithm>
#include <cstdio>

#include <json.hpp>
#include <switch.h>

#include "io/Binary.h"
#include "platform/Files.h"

namespace openshiina::player {

using Json = nlohmann::ordered_json;

// The C# player's labels and help lines (Localization/Strings.resx)
const ScalingChoice kScalingChoices[5] = {
    {Scaling::Sharp, "Sharp", "Sharp", "Whole game pixels as far as they fit, the rest smooth (even text and lines)."},
    {Scaling::WholeNumbers, "WholeNumbers", "Whole numbers only", "x2, x3... with black around (exact)."},
    {Scaling::Smooth, "Smooth", "Smooth", "Filtered, soft."},
    {Scaling::Nearest, "Nearest", "Nearest", "The screen's size, pixels as they fall (uneven when not a whole number)."},
    {Scaling::Fsr, "Fsr", "FSR 1", "AMD's upscaler, clean edges, sharpened as set below (the GPU's work)."},
};

// The C# player's labels and help lines (Localization/Strings.resx)
const FileCacheChoice kFileCacheChoices[6] = {
    {"Auto", "Auto", "By the device's memory: a thirty-second of it, 64 MB to 512 MB.", 0},
    {"Potato", "Potato (32 MB)", "For the oldest devices: pictures over 16 MB are read again each time.", 32},
    {"Low", "Low (64 MB)", "Light: pictures over 32 MB are read again each time.", 64},
    {"Medium", "Medium (128 MB)", "Pictures of up to 64 MB are kept.", 128},
    {"High", "High (256 MB)", "The largest event pictures are kept too.", 256},
    {"Max", "Max (512 MB)", "For devices with plenty of memory.", 512},
};

int64_t Settings::FileCacheBytes() const
{
    constexpr int64_t MB = int64_t(1) << 20;
    int megabytes = kFileCacheChoices[std::clamp(FileCache, 0, 5)].Megabytes;
    if (megabytes != 0)
        return megabytes * MB;
    // Auto (C# FileCachePresets.Auto): a thirty-second of the memory, in steps of 16 MB
    u64 total = 0;
    if (R_FAILED(svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0)) || total == 0)
        return 64 * MB;
    return std::clamp(int64_t(total) / 32 / (16 * MB) * (16 * MB), 64 * MB, 512 * MB);
}

namespace {

Json ReadJson(const std::string& path)
{
    Bytes data;
    if (!Files::ReadAll(path, data))
        return Json::object();
    Json json = Json::parse(data.begin(), data.end(), nullptr, false);
    return json.is_object() ? json : Json::object();
}

} // namespace

Settings Settings::Load(const std::string& path)
{
    Settings settings;
    Json json = ReadJson(path);
    if (auto s = json.find("Scaling"); s != json.end() && s->is_string())
        for (const auto& choice : kScalingChoices)
            if (s->get<std::string>() == choice.Name)
                settings.PictureScaling = choice.Value;
    if (auto f = json.find("FsrSharpness"); f != json.end() && f->is_number_integer())
        settings.FsrSharpness = std::clamp(f->get<int>(), 1, 10);
    if (auto c = json.find("FileCache"); c != json.end() && c->is_string())
        for (int k = 0; k < 6; k++)
            if (c->get<std::string>() == kFileCacheChoices[k].Name)
                settings.FileCache = k;
    return settings;
}

void Settings::Save(const std::string& path) const
{
    Json json = ReadJson(path);
    for (const auto& choice : kScalingChoices)
        if (choice.Value == PictureScaling)
            json["Scaling"] = choice.Name;
    json["FsrSharpness"] = FsrSharpness;
    json["FileCache"] = kFileCacheChoices[std::clamp(FileCache, 0, 5)].Name;
    std::string text = json.dump(2);
    if (std::FILE* f = Files::Open(path, "wb"))
    {
        std::fwrite(text.data(), 1, text.size(), f);
        std::fclose(f);
    }
}

} // namespace openshiina::player
