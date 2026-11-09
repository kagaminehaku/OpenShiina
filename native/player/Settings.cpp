// The Switch player's settings (see Settings.h)
#include "Settings.h"

#include <algorithm>
#include <cstdio>

#include <json.hpp>

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
    return settings;
}

void Settings::Save(const std::string& path) const
{
    Json json = ReadJson(path);
    for (const auto& choice : kScalingChoices)
        if (choice.Value == PictureScaling)
            json["Scaling"] = choice.Name;
    json["FsrSharpness"] = FsrSharpness;
    std::string text = json.dump(2);
    if (std::FILE* f = Files::Open(path, "wb"))
    {
        std::fwrite(text.data(), 1, text.size(), f);
        std::fclose(f);
    }
}

} // namespace openshiina::player
