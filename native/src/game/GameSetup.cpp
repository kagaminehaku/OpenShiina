#include "game/GameSetup.h"

#include <cmath>
#include <regex>
#include <string_view>

#include "platform/Files.h"
#include "text/Text.h"

namespace openshiina::game {

namespace {

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

int Number(const std::string& ini, const std::string& key, int fallback)
{
    std::string value = Match(ini, ("(?:^|\\n)" + key + "=(\\d+)").c_str());
    return value.empty() ? fallback : std::stoi(value);
}

} // namespace

std::string GameSetup::FolderName(const std::string& game)
{
    std::string name = game;
    for (char& c : name)
        if (uint8_t(c) < 32 || std::string_view("\"<>|:*?\\/").find(c) != std::string_view::npos)
            c = '_';
    return name;
}

GameSetup GameSetup::Read(GameData& data, const std::string& savesRoot)
{
    std::string ini;
    if (auto path = data.LooseFile("RIO.INI"))
    {
        Bytes bytes;
        if (Files::ReadAll(*path, bytes))
            ini = Text::Cp932ToUtf8(bytes.data(), bytes.size());
    }
    GameSetup setup;
    setup.Version = Match(ini, R"(v(\d+\.\d+))");
    if (setup.Version.empty())
        throw InvalidDataError("RIO.INI is missing or names no engine version.");
    setup.StartScript = Trim(Match(ini, R"((?:^|\n)Scn=([^\n]+))"));
    if (setup.StartScript.empty())
        setup.StartScript = "autoexec.scn";     // the engine's own default
    setup.Game = data.SchemeName();
    setup.Width = Number(ini, "WindowWidth", 800);
    setup.Height = Number(ini, "WindowHeight", 600);
    setup.SaveFolder = Files::Combine(savesRoot, FolderName(data.SchemeName()));
    Files::CreateDirectories(setup.SaveFolder);
    return setup;
}

std::unique_ptr<scripting::ScnVm> GameSetup::CreateVm(scripting::IScnHost& host, const std::string& dataFolder) const
{
    std::string tables = Files::Combine(dataFolder, "ScnOps");
    const auto& opcodes = scripting::ScnOpcodes::ForVersion(tables, Version);
    auto vm = std::make_unique<scripting::ScnVm>(opcodes, host);
    vm->Game = Game;
    vm->EngineVersion = int(std::lround(std::stod(Version) * 100));
    vm->ScreenWidth = Width;
    vm->ScreenHeight = Height;
    if (!vm->LoadModule(0, Text::Utf8ToCp932(StartScript), true))
        throw InvalidDataError(StartScript + " is missing.");
    return vm;
}

} // namespace openshiina::game
