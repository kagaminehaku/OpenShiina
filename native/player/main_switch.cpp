// OpenShiina.nro: the player on the Switch. It shows its logo at once (ui/Renderer, deko3d), then the
// games of sdmc:/OpenShiina/Games and of the console's storage to pick from (GameList; a folder
// named as the first argument starts at once), the one played last first; X opens the settings
// (settings.json), R moves the game chosen between the SD card and the console's storage (GameMove),
// L opens its own screen (its play time and sizes, its scheme chosen, deleting it), and the game's
// picture fills the screen as the setting "Scaling" says. Its
// saves go to sdmc:/OpenShiina/Saves/<game>; Formats.Json, the opcode tables, the shaders and the
// logo are in the romfs. What it prints goes to sdmc:/OpenShiina/player.log and, started by
// nxlink -s, to the PC.
//
// Input, as the C# player's touch bar and joystick give it: a touch is the mouse (moved there, the
// left button held while touching); A is Return, B the right button, X Escape, Y Space, R / ZR
// Control (held: skip), L Page Up, the D-pad the arrow keys, the left stick moves the pointer (shown
// while it moves); + opens the menu (GameMenu: back, the scaling, leave the game). A message box
// of the game is asked over its picture (MessageDialog). Text is drawn
// with the Switch's own Japanese font
// (FreeTypeFonts); the sound goes out through SDL (only its audio is used).
#include <SDL.h>
#include <malloc.h>
#include <switch.h>
#include <sys/stat.h>
#include <unistd.h>
#include <unwind.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <format>
#include <functional>
#include <map>
#include <memory>
#include <new>
#include <optional>
#include <string>
#include <vector>

#include "DevHostReturn.h"
#include "FreeTypeFonts.h"
#include "GameList.h"
#include "GameMove.h"
#include "Strings.h"
#include "Settings.h"
#include "ui/DekoAccelerator.h"
#include "archives/FormatManager.h"
#include "audio/ScnMixer.h"
#include "audio/ScnMusic.h"
#include "audio/ScnSound.h"
#include "formats/ExeIcon.h"
#include "formats/Vc1Video.h"
#include "game/GameData.h"
#include "game/GameSetup.h"
#include "platform/DibShapes.h"
#include "platform/Files.h"
#include "scripting/ScnAddressSpace.h"
#include "scripting/ScnVm.h"
#include "text/Text.h"
#include "ui/Renderer.h"
#include "ui/UiText.h"

using namespace openshiina;
using namespace openshiina::scripting;
using player::Rect;
using player::Tr;
using player::Trf;

namespace {

constexpr const char* kGames = "sdmc:/OpenShiina/Games";
// The console's own storage (DBI's "Nand USER"), mounted as user: when it can be (written only by a move)
constexpr const char* kInternalGames = "user:/OpenShiina/Games";
constexpr const char* kLibrary = "sdmc:/OpenShiina/library.tsv";
// when each game was played last, for how long, the scheme chosen for it (GameList)
constexpr const char* kPlayed = "sdmc:/OpenShiina/played.tsv";
constexpr const char* kSettings = "sdmc:/OpenShiina/settings.json";
constexpr const char* kGpuCheck = "sdmc:/OpenShiina/gpu-check";
constexpr const char* kGpuOn = "sdmc:/OpenShiina/gpu";
constexpr const char* kTextCheck = "sdmc:/OpenShiina/text-check";

/// A switch file of the SD card: there and not starting with '0' (devhost can write one, not delete it).
bool SwitchOn(const char* path)
{
    std::FILE* f = Files::Open(path, "rb");
    if (!f)
        return false;
    int c = std::fgetc(f);
    std::fclose(f);
    return c != '0';
}

/// A switch file of the SD card for something on by default: there and starting with '0'.
bool SwitchOff(const char* path)
{
    std::FILE* f = Files::Open(path, "rb");
    if (!f)
        return false;
    int c = std::fgetc(f);
    std::fclose(f);
    return c == '0';
}
constexpr const char* kSaves = "sdmc:/OpenShiina/Saves";
constexpr const char* kLog = "sdmc:/OpenShiina/player.log";
constexpr const char* kData = "romfs:/Data";

int s_nxlink = -1;
std::FILE* s_log = nullptr;
// The GPU mode, made by the first game that uses it and kept for the next: it runs on the screen's
// deko3d device, so main lets it go before the screen (left to exit(), its queue was already gone
// and deko3d stopped the title: "an error has occurred" after every game)
std::unique_ptr<player::DekoAccelerator> s_gpu;

void Log(const char* format, ...)
{
    va_list args;
    if (s_log)
    {
        va_start(args, format);
        std::vfprintf(s_log, format, args);
        va_end(args);
        std::fflush(s_log);
    }
    if (s_nxlink >= 0)
    {
        va_start(args, format);
        std::vfprintf(stderr, format, args);
        va_end(args);
    }
}

/// The size of the allocation that failed last (operator new below), for the log.
size_t s_failedSize = 0;

/// How much memory the program has and uses (applet mode, from the Album, gives it far less than a
/// game's: started from the OpenShiina title it has all of it).
/// The heap in use (malloc's blocks: the scripts' memory, the files kept, the pictures...): the
/// process's own count is the whole heap, which the player takes at the start.
size_t HeapInUse() { return mallinfo().uordblks; }

void LogMemory(const char* when)
{
    u64 total = 0, used = 0;
    svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    Log("[memory] %s: %llu MB used of %llu MB, %zu MB of the heap in use\n", when, (unsigned long long)(used >> 20),
        (unsigned long long)(total >> 20), HeapInUse() >> 20);
}

/// A colour as the Renderer takes it, 0xAABBGGRR.
constexpr uint32_t Rgb(uint32_t r, uint32_t g, uint32_t b, uint32_t a = 255) { return a << 24 | b << 16 | g << 8 | r; }

// The logo's colours (C# PlayerTheme's Shiina theme)
constexpr uint32_t kBackground = Rgb(26, 10, 16), kPanel = Rgb(44, 20, 28), kAccent = Rgb(200, 30, 58), kText = Rgb(242, 230, 201),
                   kDim = Rgb(170, 150, 140);

// The buttons' symbols of the Switch's font (Nintendo's extension)
constexpr const char* kButtonA = "\xEE\x83\xA0";
constexpr const char* kButtonB = "\xEE\x83\xA1";
constexpr const char* kButtonX = "\xEE\x83\xA2";
constexpr const char* kButtonY = "\xEE\x83\xA3";
constexpr const char* kButtonL = "\xEE\x83\xA4";
constexpr const char* kButtonR = "\xEE\x83\xA5";
// (arrows of the Japanese font: left, right)
constexpr const char* kButtonLeftRight = "\xE2\x86\x90\xE2\x86\x92";
constexpr const char* kButtonPlus = "\xEE\x83\xAF";
constexpr const char* kButtonMinus = "\xEE\x83\xB0";

/// The buttons' hints at the foot of a screen: each button's symbol and its words.
std::string Hints(std::initializer_list<std::pair<const char*, const char*>> hints)
{
    std::string line;
    for (const auto& [button, words] : hints)
        line += (line.empty() ? "" : "     ") + std::string(button) + " " + Tr(words);
    return line;
}

/// A button as a key: its virtual key and scan code.
struct KeyOf
{
    u64 Button;
    int VirtualKey, ScanCode;
};

constexpr KeyOf kKeys[] = {
    {HidNpadButton_A, 0x0D, 0x1C},     {HidNpadButton_X, 0x1B, 0x01},    {HidNpadButton_Y, 0x20, 0x39},  {HidNpadButton_R, 0x11, 0x1D},
    {HidNpadButton_ZR, 0x11, 0x1D},    {HidNpadButton_L, 0x21, 0x49},    {HidNpadButton_Left, 0x25, 0x4B}, {HidNpadButton_Up, 0x26, 0x48},
    {HidNpadButton_Right, 0x27, 0x4D}, {HidNpadButton_Down, 0x28, 0x50},
};

/// The player's screen, text, logo and controls.
struct Ui
{
    player::Renderer Screen;
    player::UiText Text{Screen};
    player::Texture Logo = Screen.LoadImage("romfs:/ui/logo.rgba");
    player::Texture Wordmark = Screen.LoadImage("romfs:/ui/wordmark.rgba");
    PadState Pad{};

    Ui()
    {
        padConfigureInput(1, HidNpadStyleSet_NpadStandard);
        padInitializeDefault(&Pad);
        hidInitializeTouchScreen();
    }

    /// The first touch on the screen, if any (in screen pixels).
    bool Touch(float& x, float& y)
    {
        HidTouchScreenState state{};
        if (hidGetTouchScreenStates(&state, 1) == 0 || state.count == 0)
            return false;
        x = float(state.touches[0].x);
        y = float(state.touches[0].y);
        return true;
    }

    /// The logo in the middle, a line under it: while something loads.
    void Splash(const std::string& line = {})
    {
        Screen.Begin(kBackground);
        Screen.Image(Logo, {(1280 - 200) / 2.0f, 170, 200, 200});
        float w = 480, h = Wordmark.Valid() ? w * float(Wordmark.Height) / float(Wordmark.Width) : 0;
        Screen.Image(Wordmark, {(1280 - w) / 2, 400, w, h});
        if (!line.empty())
        {
            float tw = Text.Measure(line, 24);
            Text.Draw(line, (1280 - tw) / 2, 520, 24, kDim);
        }
        Screen.End();
    }
};

/// The platform for the interpreter: files of the game, saves on the SD card, the clock, input.
class SwitchHost : public IScnHost
{
public:
    SwitchHost(game::GameData& data, std::string saves) : m_data(data), m_saves(std::move(saves)), m_sound(m_mixer), m_music(m_mixer) {}

    /// The mix of the scripts' sounds, read by the sound device's thread.
    audio::ScnMixer& Mixer() { return m_mixer; }

    // Input as the window gives it (held keys by virtual key, the mouse in the game's pixels)
    bool Keys[256] = {};
    int Buttons = 0;
    int MouseX = 0, MouseY = 0;

    bool ReadFile(const std::string& name, Bytes& data) override { return m_data.Read(Slashes(Text::Cp932ToUtf8(name)), data); }
    std::optional<int64_t> ArchiveFileSize(const std::string& name) override { return m_data.Size(Slashes(Text::Cp932ToUtf8(name))); }
    std::optional<int64_t> ArchiveFileSize(const std::string& archive, const std::string& name) override
    {
        return m_data.Size(Text::Cp932ToUtf8(archive), Slashes(Text::Cp932ToUtf8(name)));
    }
    bool MountArchive(const std::string& archive, bool mounted) override { return m_data.Mount(Text::Cp932ToUtf8(archive), mounted); }
    bool ExecutableCopy(const std::string& name, Bytes& data) override { return m_data.ExecutableCopy(LooseFileSize(name), data); }

    bool ReadLooseFile(const std::string& name, Bytes& data) override
    {
        auto path = m_data.LooseFile(Text::Cp932ToUtf8(name));
        return path && Files::ReadAll(*path, data);
    }

    // The file's size from the file system, as scnrun's: read whole it is too much for the Switch
    // (the install check asks for the archives', 1.8 GB for Sorcery Jokers' SJSe_G1.WAR)
    std::optional<int64_t> LooseFileSize(const std::string& name) override
    {
        Bytes bytes;
        if (Text::EqualsIgnoreCase(Text::Cp932ToUtf8(name), "RIO.INI"))
            return ReadLooseFile(name, bytes) ? std::optional<int64_t>(int64_t(bytes.size())) : std::nullopt;
        auto path = m_data.LooseFile(Text::Cp932ToUtf8(name));
        struct stat info;
        if (!path || stat(path->c_str(), &info) != 0)
            return std::nullopt;
        return int64_t(info.st_size);
    }

    uint32_t Milliseconds() override { return uint32_t(armTicksToNs(armGetSystemTick()) / 1000000); }
    void SetTitle(const std::string& title) override { Log("[title] %s\n", Text::Cp932ToUtf8(title).c_str()); }
    void SetFullScreen(bool) override {}
    std::string SaveFolder() override { return m_saves; }

    bool ReadSaveFile(const std::string& name, Bytes& data) override { return Files::ReadAll(SavePath(name), data); }

    void WriteSaveFile(const std::string& name, const Bytes& bytes) override
    {
        if (std::FILE* f = Files::Open(SavePath(name), "wb"))
        {
            std::fwrite(bytes.data(), 1, bytes.size(), f);
            std::fclose(f);
        }
    }

    void DeleteSaveFile(const std::string& name) override { std::remove(SavePath(name).c_str()); }

    /// MessageBoxA asked of the player (Play's MessageDialog); without it the answer is yes / OK.
    std::function<int(const std::string& text, const std::string& caption, int type)> AskPlayer;

    int MessageBox(const std::string& text, const std::string& caption, int type) override
    {
        Log("[message box %d] %s\n", type, Text::Cp932ToUtf8(text).c_str());
        int answer = AskPlayer ? AskPlayer(text, caption, type) : (type & 0xF) == 4 || (type & 0xF) == 3 ? 6 : 1;
        Log("[message box] answered %d\n", answer);
        return answer;
    }

    IScnShapes* Shapes() override { return &m_shapes; }
    IScnSound* Sound() override { return &m_sound; }
    void PauseSound(bool paused) override { m_mixer.Paused = paused; }
    IScnMusic* Music() override { return &m_music; }
    IScnFonts* Fonts() override { return m_fonts.Ready() ? &m_fonts : nullptr; }
    bool KeyDown(int virtualKey) override { return virtualKey >= 0 && virtualKey < 256 && Keys[virtualKey]; }
    int MouseButtons() override { return Buttons; }
    std::pair<int, int> MousePosition() override { return {MouseX, MouseY}; }

    void SetMousePosition(int x, int y) override
    {
        MouseX = x;
        MouseY = y;
    }

private:
    static std::string Slashes(std::string name)
    {
        for (char& c : name)
            if (c == '/')
                c = '\\';
        return name;
    }

    std::string SavePath(const std::string& name) { return Files::Combine(m_saves, Files::FileName(Text::Cp932ToUtf8(name))); }

    game::GameData& m_data;
    std::string m_saves;
    platform::DibShapes m_shapes;
    player::FreeTypeFonts m_fonts;
    audio::ScnMixer m_mixer;
    audio::ScnSound m_sound;
    audio::ScnMusic m_music;
};

/// What PickGame gives for - : the homebrew menu next, in place of a game.
constexpr const char* kToHbmenu = "\x01hbmenu";

/// The settings (settings.json), read at the start
player::Settings s_settings;

/// The icons of the games' .exe files, read once each (by the .exe's path), none for one without
std::map<std::string, player::Texture> s_icons;

/// The icon of a game for the list, read the first time it is asked for (one a frame: reading an
/// .exe from the card takes a moment); false when it is not read yet.
bool IconOf(Ui& ui, const player::GameEntry& game, bool& readOne, player::Texture& icon)
{
    if (game.Exe.empty())
        return true;
    auto found = s_icons.find(game.Exe);
    if (found == s_icons.end())
    {
        if (readOne)
            return false;
        readOne = true;
        player::Texture t;
        if (auto image = formats::ExeIcon::Read(game.Exe, 48))
        {
            t = ui.Screen.CreateTexture(image->Width, image->Height);
            for (int y = 0; y < image->Height; y++)
                std::memcpy(t.Pixels + size_t(y) * t.Stride, image->Rgba.data() + size_t(y) * size_t(image->Width) * 4, size_t(image->Width) * 4);
        }
        found = s_icons.emplace(game.Exe, t).first;
    }
    icon = found->second;
    return true;
}

/// Text in lines no wider than width: its own line breaks kept, a longer line broken after its last
/// space that fits or, without one (Japanese), after the last character that fits.
std::vector<std::string> WrapText(Ui& ui, const std::string& text, float size, float width)
{
    std::vector<std::string> lines;
    size_t start = 0;
    while (true)
    {
        size_t end = text.find('\n', start);
        std::string paragraph = text.substr(start, end == std::string::npos ? std::string::npos : end - start);
        if (!paragraph.empty() && paragraph.back() == '\r')
            paragraph.pop_back();
        std::string line;
        for (size_t at = 0; at < paragraph.size();)
        {
            size_t from = at;
            player::NextCodepoint(paragraph, at);
            std::string character = paragraph.substr(from, at - from);
            if (line.empty() || ui.Text.Measure(line + character, size) <= width)
            {
                line += character;
                continue;
            }
            size_t space = line.rfind(' ');
            if (space != std::string::npos && space > 0)
            {
                lines.push_back(line.substr(0, space));
                line = line.substr(space + 1) + character;
            }
            else
            {
                lines.push_back(line);
                line = character;
            }
        }
        lines.push_back(line);
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    while (lines.size() > 1 && lines.back().empty())
        lines.pop_back();
    return lines;
}

/// The setting "Language" and the Vietnamese screens (Strings.cpp): off for now, the screens in
/// English (the Switch's fonts lack most of Vietnamese's letters; a font of its own is to come).
constexpr bool kLanguages = false;

/// The settings, as the C# player has them ("Scaling", "FSR sharpness" and "Show the frame rate" of
/// its Graphics page, "File cache", "Language" with kLanguages; the file cache counts from the next
/// game started):
/// up / down choose a row, left / right (or A, or a touch on the row's two halves) change it; B or
/// X goes back, and the file is written if anything changed.
void SettingsScreen(Ui& ui)
{
    constexpr int Rows = kLanguages ? 5 : 4;
    constexpr float RowHeight = 76, ListX = 380, ListY = 120, ListW = 860;
    constexpr int LanguageCount = int(std::size(player::kLanguageChoices));
    constexpr int ScalingCount = int(std::size(player::kScalingChoices));
    player::Settings before = s_settings;
    int row = 0;
    bool touching = false;
    auto scalingIndex = [] {
        for (int k = 0; k < ScalingCount; k++)
            if (player::kScalingChoices[k].Value == s_settings.PictureScaling)
                return k;
        return 0;
    };
    auto languageIndex = [] {
        for (int k = 0; k < LanguageCount; k++)
            if (s_settings.Language == player::kLanguageChoices[k].Code)
                return k;
        return 0;
    };
    auto change = [&](int r, int step) {
        if (r == 4)
        {
            s_settings.Language = player::kLanguageChoices[(languageIndex() + step + LanguageCount) % LanguageCount].Code;
            player::SetLanguage(s_settings.Language);
        }
        else if (r == 0)
            s_settings.PictureScaling = player::kScalingChoices[(scalingIndex() + step + ScalingCount) % ScalingCount].Value;
        else if (r == 1)
            s_settings.FsrSharpness = std::clamp(s_settings.FsrSharpness + step, 1, 10);
        else if (r == 2)
            s_settings.FileCache = std::clamp(s_settings.FileCache + step, 0, int(std::size(player::kFileCacheChoices)) - 1);
        else if (r == 3)
            s_settings.ShowFrameRate = !s_settings.ShowFrameRate;
    };
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & (HidNpadButton_B | HidNpadButton_X | HidNpadButton_Plus))
            break;
        if ((down & HidNpadButton_AnyDown) && row + 1 < Rows)
            row++;
        if ((down & HidNpadButton_AnyUp) && row > 0)
            row--;
        if (down & (HidNpadButton_AnyRight | HidNpadButton_A))
            change(row, 1);
        if (down & HidNpadButton_AnyLeft)
            change(row, -1);
        float tx, ty;
        bool touch = ui.Touch(tx, ty);
        if (touch && !touching && tx >= ListX && tx < ListX + ListW && ty >= ListY && ty < ListY + Rows * RowHeight)
        {
            row = int((ty - ListY) / RowHeight);
            change(row, tx < ListX + ListW / 2 ? -1 : 1);
        }
        touching = touch;

        player::Renderer& s = ui.Screen;
        s.Begin(kBackground);
        s.Fill({0, 0, 340, 720}, kPanel);
        s.Image(ui.Logo, {90, 60, 160, 160});
        float ww = 280, wh = ui.Wordmark.Valid() ? ww * float(ui.Wordmark.Height) / float(ui.Wordmark.Width) : 0;
        s.Image(ui.Wordmark, {30, 240, ww, wh});
        ui.Text.Draw(Tr("Settings"), ListX, 48, 34, kText);
        const auto& scaling = player::kScalingChoices[scalingIndex()];
        const auto& cache = player::kFileCacheChoices[s_settings.FileCache];
        std::string cacheLabel = cache.Megabytes != 0 ? Tr(cache.Label) : Trf("Auto ({} MB here)", s_settings.FileCacheBytes() >> 20);
        std::string values[5] = {Tr(scaling.Label), std::to_string(s_settings.FsrSharpness), cacheLabel,
                                 Tr(s_settings.ShowFrameRate ? "On" : "Off"), Tr(player::kLanguageChoices[languageIndex()].Name)};
        const char* names[5] = {"Scaling", "FSR sharpness", "File cache", "Show the frame rate", "Language"};
        for (int r = 0; r < Rows; r++)
        {
            float y = ListY + float(r) * RowHeight;
            if (r == row)
                s.Fill({ListX - 12, y, ListW, RowHeight - 12}, kAccent, 12);
            ui.Text.Draw(Tr(names[r]), ListX + 8, y + 16, 28, kText);
            std::string value = std::string("<  ") + values[r] + "  >";
            ui.Text.Draw(value, ListX + ListW - 40 - ui.Text.Measure(value, 28), y + 16, 28, kText);
        }
        // what the chosen row's value does
        const char* help = row == 0   ? scaling.Help
                           : row == 1 ? "1 mild ... 10 strongest (for FSR 1 only)."
                           : row == 2 ? cache.Help
                           : row == 3 ? "Frames a second and the slowest frame, over the game's corner."
                                      : "The words of OpenShiina's own screens. The games keep their own.";
        float helpY = ListY + Rows * RowHeight + 12;
        for (const auto& line : WrapText(ui, Tr(help), 24, ListW - 20))
        {
            ui.Text.Draw(line, ListX + 8, helpY, 24, kDim);
            helpY += 34;
        }
        if (row == 2)
            ui.Text.Draw(Tr("From the next game started."), ListX + 8, helpY, 24, kDim);
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = Hints({{kButtonLeftRight, "Change"}, {kButtonB, "Back"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    if (s_settings.PictureScaling != before.PictureScaling || s_settings.FsrSharpness != before.FsrSharpness
        || s_settings.FileCache != before.FileCache || s_settings.ShowFrameRate != before.ShowFrameRate || s_settings.Language != before.Language)
        s_settings.Save(kSettings);
}

/// The rail of the list's screens: the logo and the wordmark.
void DrawRail(Ui& ui)
{
    player::Renderer& s = ui.Screen;
    s.Fill({0, 0, 340, 720}, kPanel);
    s.Image(ui.Logo, {90, 60, 160, 160});
    float ww = 280, wh = ui.Wordmark.Valid() ? ww * float(ui.Wordmark.Height) / float(ui.Wordmark.Width) : 0;
    s.Image(ui.Wordmark, {30, 240, ww, wh});
}

/// A title and its lines beside the rail (each wrapped to the width): true for A, false for B (or + /
/// leaving); only A when choice is false (a message).
bool Ask(Ui& ui, const std::string& title, const std::vector<std::string>& lines, bool choice)
{
    constexpr float ListX = 380, ListW = 860;
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & HidNpadButton_A)
            return true;
        if (down & (HidNpadButton_B | HidNpadButton_Plus))
            return !choice;
        player::Renderer& s = ui.Screen;
        s.Begin(kBackground);
        DrawRail(ui);
        ui.Text.Draw(title, ListX, 48, 34, kText);
        float y = 120;
        for (const auto& line : lines)
            for (const auto& part : WrapText(ui, line, 26, ListW))
            {
                ui.Text.Draw(part, ListX, y, 26, kText);
                y += 40;
            }
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = choice ? Hints({{kButtonA, "Go on"}, {kButtonB, "Cancel"}}) : Hints({{kButtonA, "OK"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    return false;
}

std::string SizeText(int64_t bytes)
{
    constexpr double MB = 1024.0 * 1024.0;
    if (bytes < int64_t(1) << 20)
        return std::format("{} KB", (bytes + 1023) / 1024);
    return bytes >= int64_t(1) << 30 ? std::format("{:.1f} GB", double(bytes) / (MB * 1024.0)) : std::format("{:.0f} MB", double(bytes) / MB);
}

/// A play time as the C# library tells it: "under a minute", "45 min", "14 h", "14 h 20 min".
std::string DurationText(int64_t seconds)
{
    int64_t minutes = seconds / 60, hours = minutes / 60;
    if (minutes == 0)
        return Tr("under a minute");
    return hours == 0 ? Trf("{} min", minutes) : minutes % 60 == 0 ? Trf("{} h", hours) : Trf("{} h {} min", hours, minutes % 60);
}

/// The date of a time (seconds since 1970) on the console's clock in its own time zone (newlib's
/// localtime knows no zone), as days since 1970.
int64_t LocalDay(int64_t when, TimeCalendarTime& date)
{
    TimeCalendarAdditionalInfo info{};
    if (R_FAILED(timeToCalendarTimeWithMyRule(u64(when), &date, &info)))
    {
        std::time_t t = std::time_t(when);
        std::tm utc = *std::gmtime(&t);
        date.year = u16(utc.tm_year + 1900);
        date.month = u8(utc.tm_mon + 1);
        date.day = u8(utc.tm_mday);
    }
    // (the days of a date, as Howard Hinnant's days_from_civil)
    int y = date.year - (date.month <= 2 ? 1 : 0), m = date.month, d = date.day;
    int era = (y >= 0 ? y : y - 399) / 400;
    int yoe = y - era * 400, doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1, doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return int64_t(era) * 146097 + doe - 719468;
}

/// When a game was played last, as the C# library tells it: "just now", "2 hours ago", "yesterday",
/// "5 days ago", then the date.
std::string AgoText(int64_t when)
{
    int64_t now = int64_t(std::time(nullptr)), past = now - when;
    if (past < 60)
        return Tr("just now");
    if (past < 3600)
        return past < 120 ? Tr("a minute ago") : Trf("{} minutes ago", past / 60);
    TimeCalendarTime today{}, day{};
    int64_t days = LocalDay(now, today) - LocalDay(when, day);
    if (days <= 0)
        return past < 7200 ? Tr("an hour ago") : Trf("{} hours ago", past / 3600);
    if (days == 1)
        return Tr("yesterday");
    if (days < 7)
        return Trf("{} days ago", days);
    return Trf("on {}", std::format("{:04}-{:02}-{:02}", int(day.year), int(day.month), int(day.day)));
}

/// Where the player keeps a game's saves (GameSetup::Read's folder).
std::string SaveFolderOf(const player::GameEntry& game) { return Files::Combine(kSaves, game::GameSetup::FolderName(game.Name)); }

/// Moves the game between the SD card and the console's storage (R in the list): measured, asked, the
/// warning shown before the first one onto the console, copied with a bar (B cancels), then the old
/// folder deleted. True when it moved.
bool MoveGame(Ui& ui, const player::GameEntry& game)
{
    // the space kept free on the console's storage, for the system's saves and updates
    constexpr int64_t ConsoleReserve = int64_t(1) << 30;
    bool toConsole = !game.Internal;
    std::string folder = Files::FileName(game.Path);
    std::string target = Files::Combine(toConsole ? kInternalGames : kGames, folder);
    std::string where = Tr(toConsole ? "the console's storage" : "the SD card");
    auto tell = [&](const std::string& line) { Ask(ui, Tr("Move"), {game.Name, line}, false); };
    if (Files::IsDirectory(target) || Files::IsFile(target))
    {
        tell(Trf("{} has a folder named {} already.", where, folder));
        return false;
    }
    ui.Splash(Trf("Measuring {}...", folder));
    auto size = player::MeasureFolder(game.Path);
    if (!size.Readable)
    {
        tell(Tr("Not all of its folder can be read."));
        return false;
    }
    if (toConsole && size.HugeFile)
    {
        tell(Tr("It has a file of 4 GB or more: the console's storage (FAT32) cannot hold it."));
        return false;
    }
    int64_t free = player::FreeSpace(toConsole ? "user" : "sdmc");
    int64_t reserve = toConsole ? ConsoleReserve : 0;
    if (free >= 0 && size.Bytes + reserve > free)
    {
        tell(Trf(toConsole ? "It needs {} and 1 GB kept free; {} has {} free." : "It needs {}; {} has {} free.", SizeText(size.Bytes), where,
                 SizeText(free)));
        return false;
    }
    if (toConsole && !s_settings.ConsoleWarned)
    {
        std::string kind = player::ConsoleStorageKind();
        std::vector<std::string> lines = {
            Trf("The game goes into the console's own storage{}: its USER partition, beside the system's games and saves. Before the first one:",
                kind.empty() ? std::string() : " (" + kind + ")"),
            Tr("- Keep the console charged: switched off while copying, that partition can be damaged."),
            Tr("- 1 GB is kept free there: the system needs room for its saves and updates."),
            Tr("- A format or a factory reset of the console deletes the games there."),
            Tr("- Only the folder OpenShiina/Games is written; nothing else there changes."),
            Tr("This is shown once.")};
        if (!Ask(ui, Tr("The console's storage"), lines, true))
            return false;
        s_settings.ConsoleWarned = true;
        s_settings.Save(kSettings);
    }
    std::vector<std::string> lines = {game.Name, Trf("{} in {} files, to {}.", SizeText(size.Bytes), size.Files, where)};
    if (free >= 0)
        lines.push_back(Trf("Free there now: {}.", SizeText(free)));
    lines.push_back(Tr("The game is copied, then deleted from where it was."));
    if (!Ask(ui, Tr("Move"), lines, true))
        return false;

    if (toConsole)
        Files::CreateDirectories(kInternalGames);
    // (the console does not sleep by itself while it copies)
    appletSetMediaPlaybackState(true);
    uint64_t drawn = 0, started = armGetSystemTick();
    std::string error;
    bool copied = player::CopyFolder(game.Path, target, [&](int64_t done) {
        padUpdate(&ui.Pad);
        if ((padGetButtonsDown(&ui.Pad) & HidNpadButton_B) || !appletMainLoop())
            return false;
        uint64_t now = armGetSystemTick();
        if (drawn != 0 && armTicksToNs(now - drawn) < 100000000)
            return true;
        drawn = now;
        constexpr float ListX = 380, ListW = 860;
        player::Renderer& s = ui.Screen;
        s.Begin(kBackground);
        DrawRail(ui);
        ui.Text.Draw(Trf("Moving to {}", where), ListX, 48, 34, kText);
        ui.Text.Draw(ui.Text.Fit(game.Name, 28, ListW), ListX, 130, 28, kText);
        float part = size.Bytes > 0 ? float(double(done) / double(size.Bytes)) : 1.0f;
        s.Fill({ListX, 200, ListW, 28}, kPanel, 14);
        s.Fill({ListX, 200, std::max(28.0f, ListW * std::min(part, 1.0f)), 28}, kAccent, 14);
        double seconds = double(armTicksToNs(now - started)) / 1e9;
        std::string line = seconds > 1 ? Trf("{} of {}, {:.0f} MB a second", SizeText(done), SizeText(size.Bytes), double(done) / (1024.0 * 1024.0) / seconds)
                                       : Trf("{} of {}", SizeText(done), SizeText(size.Bytes));
        ui.Text.Draw(line, ListX, 250, 26, kDim);
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = Hints({{kButtonB, "Cancel"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
        return true;
    }, error);
    appletSetMediaPlaybackState(false);
    double seconds = double(armTicksToNs(armGetSystemTick() - started)) / 1e9;
    if (!copied)
    {
        Log("[move] %s to %s: %s\n", game.Path.c_str(), target.c_str(), error.c_str());
        tell(error == "cancelled" ? std::string(Tr("Cancelled: it stays where it was.")) : Trf("Not moved (it stays where it was): {}.", error));
        return false;
    }
    Log("[move] %s to %s: %lld bytes in %.1f s\n", game.Path.c_str(), target.c_str(), (long long)size.Bytes, seconds);
    ui.Splash(Tr("Deleting it from where it was..."));
    if (!player::RemoveFolder(game.Path))
    {
        Log("[move] %s: not all deleted\n", game.Path.c_str());
        tell(Trf("Copied, but not all of the old folder could be deleted: {}", game.Path));
    }
    return true;
}

/// A list to choose from beside the rail (the schemes for a game): the one chosen, or -1 for B.
int ChooseFrom(Ui& ui, const std::string& title, const std::vector<std::string>& items)
{
    constexpr float RowHeight = 60, ListX = 380, ListY = 110, ListW = 860;
    constexpr int Rows = 9;
    size_t choice = 0, top = 0;
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & (HidNpadButton_B | HidNpadButton_Plus))
            return -1;
        if (down & HidNpadButton_A)
            return int(choice);
        if ((down & HidNpadButton_AnyDown) && choice + 1 < items.size())
            choice++;
        if ((down & HidNpadButton_AnyUp) && choice > 0)
            choice--;
        if (choice < top)
            top = choice;
        if (choice >= top + Rows)
            top = choice - Rows + 1;
        player::Renderer& s = ui.Screen;
        s.Begin(kBackground);
        DrawRail(ui);
        ui.Text.Draw(title, ListX, 48, 34, kText);
        for (size_t k = top; k < items.size() && k < top + Rows; k++)
        {
            float y = ListY + float(k - top) * RowHeight;
            if (k == choice)
                s.Fill({ListX - 12, y, ListW, RowHeight - 8}, kAccent, 12);
            ui.Text.Draw(ui.Text.Fit(items[k], 28, ListW - 24), ListX + 8, y + 10, 28, kText);
        }
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = Hints({{kButtonA, "Choose"}, {kButtonB, "Back"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    return -1;
}

/// The game's own screen (L in the list), as the C# library's page of a game: when it was played,
/// for how long, its folder's size and its saves', and what can be done with it: play, choose its
/// scheme (a game its .exe does not tell), delete it (its saves kept, or with them). Its answer:
enum class GameAction
{
    Back,
    Play,
    Changed,    // deleted, or a scheme chosen: the list made again
};

GameAction GameScreen(Ui& ui, const player::GameEntry& game)
{
    ui.Splash(Trf("Measuring {}...", game.Name));
    auto folder = player::MeasureFolder(game.Path);
    std::string saves = game.Known ? SaveFolderOf(game) : std::string();
    int64_t saveBytes = saves.empty() ? 0 : player::MeasureFolder(saves).Bytes;
    // the rows
    enum Row
    {
        PlayRow,
        SchemeRow,
        DeleteRow,
        DeleteAllRow,
    };
    std::vector<Row> rows;
    if (game.Known)
        rows.push_back(PlayRow);
    // (a scheme to choose only where the .exe does not tell it)
    if (!game.Known || !game.Scheme.empty())
        rows.push_back(SchemeRow);
    rows.push_back(DeleteRow);
    if (saveBytes > 0)
        rows.push_back(DeleteAllRow);
    auto label = [&](Row r) -> std::string {
        switch (r)
        {
        case PlayRow: return Tr("Play");
        case SchemeRow: return Tr(game.Scheme.empty() ? "Choose its scheme" : "Choose another scheme");
        case DeleteRow: return Trf(saveBytes > 0 ? "Delete the game (frees {}), keep its saves" : "Delete the game (frees {})", SizeText(folder.Bytes));
        case DeleteAllRow: return Trf("Delete the game and its saves ({})", SizeText(saveBytes));
        }
        return {};
    };
    std::vector<std::string> facts = {game.LastPlayed != 0 ? Trf("Played {}", AgoText(game.LastPlayed)) : std::string(Tr("Not played yet"))};
    if (game.PlaySeconds > 0)
        facts.push_back(Trf("Played for {}", DurationText(game.PlaySeconds)));
    facts.push_back(saveBytes > 0 ? Trf("Size {}, saves {}", SizeText(folder.Bytes), SizeText(saveBytes)) : Trf("Size {}, no saves yet", SizeText(folder.Bytes)));
    facts.push_back(Trf(game.Internal ? "On the console: {}" : "On the SD card: {}", game.Path));
    if (!game.Scheme.empty())
        facts.push_back(Trf("Scheme chosen: {}", game.Scheme));
    else if (!game.Known)
        facts.push_back(Tr("Not recognised: its .exe does not tell the scheme."));

    constexpr float RowHeight = 64, ListX = 380, ListW = 860;
    float rowsY = 120 + float(facts.size()) * 40 + 30;
    size_t choice = 0;
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & (HidNpadButton_B | HidNpadButton_Plus))
            return GameAction::Back;
        if ((down & HidNpadButton_AnyDown) && choice + 1 < rows.size())
            choice++;
        if ((down & HidNpadButton_AnyUp) && choice > 0)
            choice--;
        if (down & HidNpadButton_A)
        {
            Row r = rows[choice];
            if (r == PlayRow)
                return GameAction::Play;
            if (r == SchemeRow)
            {
                ui.Splash(Trf("Trying the schemes on {}...", Files::FileName(game.Path)));
                auto candidates = player::DetectSchemes(game.Path);
                Log("[schemes] %s: %zu fit\n", game.Path.c_str(), candidates.size());
                if (candidates.empty())
                {
                    Ask(ui, Tr("Choose its scheme"), {Tr("No scheme of Formats.Json decodes its archives' files.")}, false);
                    continue;
                }
                std::vector<std::string> names;
                for (const auto& c : candidates)
                    names.push_back(Trf("{}  ({} of {} files right)", c.Name, c.Right, c.Tried));
                int picked = ChooseFrom(ui, Tr("The schemes that decode it"), names);
                if (picked < 0)
                    continue;
                player::ChooseScheme(kPlayed, Files::FileName(game.Path), candidates[size_t(picked)].Name);
                return GameAction::Changed;
            }
            bool all = r == DeleteAllRow;
            std::vector<std::string> lines = {game.Name, game.Path, Tr(all ? "Its folder and its saves are deleted: they cannot be brought back."
                                                                           : "Its folder is deleted: it cannot be brought back.")};
            if (!all && saveBytes > 0)
                lines.push_back(Trf("Its saves are kept ({}).", saves));
            if (!Ask(ui, Tr("Delete the game"), lines, true))
                continue;
            // (C# GameLibrary.Delete's checks that hold here: a folder of the games' own, with archives)
            if (!game::GameData::FindArchive(game.Path))
            {
                Ask(ui, Tr("Delete the game"), {Tr("The folder has no .WAR archives in it any more, so it was left alone.")}, false);
                continue;
            }
            ui.Splash(Trf("Deleting {}...", game.Name));
            bool done = player::RemoveFolder(game.Path);
            if (done && all)
                done = player::RemoveFolder(saves);
            Log("[delete] %s%s: %s\n", game.Path.c_str(), all ? " and its saves" : "", done ? "done" : "not all");
            player::ForgetGame(kPlayed, Files::FileName(game.Path));
            Ask(ui, Tr("Delete the game"),
                {done ? Trf("{} was deleted: {} freed.", game.Name, SizeText(folder.Bytes + (all ? saveBytes : 0))) : std::string(Tr("Not everything could be deleted."))},
                false);
            return GameAction::Changed;
        }

        player::Renderer& s = ui.Screen;
        s.Begin(kBackground);
        DrawRail(ui);
        ui.Text.Draw(ui.Text.Fit(game.Name, 34, ListW), ListX, 48, 34, kText);
        for (size_t k = 0; k < facts.size(); k++)
            ui.Text.Draw(ui.Text.Fit(facts[k], 24, ListW), ListX, 120 + float(k) * 40, 24, kDim);
        for (size_t k = 0; k < rows.size(); k++)
        {
            float y = rowsY + float(k) * RowHeight;
            if (k == choice)
                s.Fill({ListX - 12, y, ListW, RowHeight - 10}, kAccent, 12);
            ui.Text.Draw(ui.Text.Fit(label(rows[k]), 28, ListW - 24), ListX + 8, y + 12, 28, kText);
        }
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = Hints({{kButtonA, "OK"}, {kButtonB, "Back"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    return GameAction::Back;
}

/// The game to play from the list (its folder), kToHbmenu for −, or nothing for + (leave). Y looks
/// at every folder again; R moves the game chosen to the other storage; L opens its own screen.
std::string PickGame(Ui& ui, bool internal)
{
    std::vector<std::pair<std::string, bool>> roots = {{kGames, false}};
    if (internal)
        roots.emplace_back(kInternalGames, true);
    // The folders new or changed since the list was made are recognised again (a game that writes
    // into its own folder changes it): a moment, shown, not a black screen
    ui.Splash(Tr("Looking at the games..."));
    uint64_t looking = armGetSystemTick();
    auto games = player::ListGames(roots, kLibrary, kPlayed, false);
    Log("[list] %zu games (%.0f ms)\n", games.size(), double(armTicksToNs(armGetSystemTick() - looking)) / 1e6);
    constexpr float RowHeight = 60, ListX = 380, ListY = 110, ListW = 860, IconSize = 44;
    constexpr int Rows = 9;
    size_t choice = 0, top = 0;
    bool touching = false;
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & HidNpadButton_Plus)
            return {};
        if ((down & HidNpadButton_Minus) && envHasNextLoad())
            return kToHbmenu;
        if (!games.empty() && (down & HidNpadButton_A))
            return games[choice].Path;
        if (down & HidNpadButton_X)
            SettingsScreen(ui);
        if (internal && !games.empty() && (down & HidNpadButton_R) && MoveGame(ui, games[choice]))
        {
            ui.Splash(Tr("Looking at the games..."));
            games = player::ListGames(roots, kLibrary, kPlayed, false);
            choice = std::min(choice, games.empty() ? size_t(0) : games.size() - 1);
            top = std::min(top, choice);
        }
        if (!games.empty() && (down & HidNpadButton_L))
        {
            GameAction action = GameScreen(ui, games[choice]);
            if (action == GameAction::Play)
                return games[choice].Path;
            if (action == GameAction::Changed)
            {
                ui.Splash(Tr("Looking at the games..."));
                games = player::ListGames(roots, kLibrary, kPlayed, false);
                choice = std::min(choice, games.empty() ? size_t(0) : games.size() - 1);
                top = std::min(top, choice);
            }
        }
        if (down & HidNpadButton_Y)
        {
            ui.Splash(Tr("Looking at the games..."));
            games = player::ListGames(roots, kLibrary, kPlayed, true);
            choice = top = 0;
        }
        if ((down & (HidNpadButton_AnyDown)) && choice + 1 < games.size())
            choice++;
        if ((down & (HidNpadButton_AnyUp)) && choice > 0)
            choice--;
        float tx, ty;
        bool touch = ui.Touch(tx, ty);
        if (touch && !touching && tx >= ListX && tx < ListX + ListW && ty >= ListY)
        {
            size_t row = top + size_t((ty - ListY) / RowHeight);
            if (row < games.size() && row < top + Rows)
            {
                if (row == choice)
                    return games[choice].Path;
                choice = row;
            }
        }
        touching = touch;
        if (choice < top)
            top = choice;
        if (choice >= top + Rows)
            top = choice - Rows + 1;

        player::Renderer& s = ui.Screen;
        s.Begin(kBackground);
        DrawRail(ui);
        // the games: the icon, the name (dim for a folder no scheme opens)
        ui.Text.Draw(Tr("Games"), ListX, 48, 34, kText);
        if (games.empty())
            ui.Text.Draw(Trf("No game folder in {} yet", kGames), ListX, ListY + 10, 24, kDim);
        bool readOne = false;
        for (size_t k = top; k < games.size() && k < top + Rows; k++)
        {
            const auto& game = games[k];
            float y = ListY + float(k - top) * RowHeight;
            if (k == choice)
                s.Fill({ListX - 12, y, ListW, RowHeight - 8}, kAccent, 12);
            player::Texture icon;
            if (IconOf(ui, game, readOne, icon) && icon.Valid())
                s.Image(icon, {ListX, y + (RowHeight - 8 - IconSize) / 2, IconSize, IconSize});
            std::string name = game.Known ? game.Name : game.Name + "  " + Tr("(not recognised)");
            if (game.Internal)
                name += std::string("  - ") + Tr("console");
            float textX = ListX + IconSize + 16, right = ListX + ListW - 24;
            // when it was played last, and for how long, at the row's end
            if (game.LastPlayed != 0)
            {
                std::string played = AgoText(game.LastPlayed) + (game.PlaySeconds >= 60 ? ", " + DurationText(game.PlaySeconds) : "");
                float w = ui.Text.Measure(played, 22);
                ui.Text.Draw(played, right - w, y + 14, 22, k == choice ? kText : kDim);
                right -= w + 24;
            }
            ui.Text.Draw(ui.Text.Fit(name, 28, right - textX), textX, y + 10, 28, game.Known ? kText : kDim);
        }
        // the buttons
        s.Fill({0, 672, 1280, 48}, kPanel);
        // (− only when a loader started the player: installed as its own title, there is no hbmenu to go to)
        std::vector<std::pair<const char*, const char*>> buttons;
        if (!games.empty())
            buttons.insert(buttons.end(), {{kButtonA, "Play"}, {kButtonL, "Game"}});
        if (internal && !games.empty())
            buttons.push_back({kButtonR, "Move"});
        buttons.insert(buttons.end(), {{kButtonY, "Look again"}, {kButtonX, "Settings"}});
        if (envHasNextLoad())
            buttons.push_back({kButtonMinus, "hbmenu"});
        buttons.push_back({kButtonPlus, "Quit"});
        std::string hints;
        for (const auto& [button, words] : buttons)
            hints += (hints.empty() ? "" : "     ") + std::string(button) + " " + Tr(words);
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    return {};
}

/// The game's window picture (BGR rows) into an RGBA texture.
void CopyPicture(const Bytes& window, int width, int height, player::Texture& t)
{
    for (int y = 0; y < height; y++)
    {
        const uint8_t* src = window.data() + size_t(y) * size_t(width) * 3;
        uint8_t* dst = t.Pixels + size_t(y) * t.Stride;
        for (int x = 0; x < width; x++, src += 3, dst += 4)
        {
            dst[0] = src[2];
            dst[1] = src[1];
            dst[2] = src[0];
            dst[3] = 0xFF;
        }
    }
}

/// The menu + opens while a game plays, over its picture (the game stands still meanwhile): back
/// to the game, the scaling and FSR's sharpness (the settings, written when they changed), or
/// leave the game. True: leave.
bool GameMenu(Ui& ui, const std::function<void()>& drawGame)
{
    // the rows
    constexpr int Back = 0, ScalingRow = 1, Sharpness = 2, Leave = 3, Rows = 4;
    constexpr float RowHeight = 64, MenuW = 620, MenuX = (1280 - MenuW) / 2, MenuY = 170;
    constexpr int ScalingCount = int(std::size(player::kScalingChoices));
    player::Settings before = s_settings;
    int row = Back;
    bool touching = false, leave = false;
    auto scalingIndex = [] {
        for (int k = 0; k < ScalingCount; k++)
            if (player::kScalingChoices[k].Value == s_settings.PictureScaling)
                return k;
        return 0;
    };
    auto change = [&](int r, int step) {
        if (r == ScalingRow)
            s_settings.PictureScaling = player::kScalingChoices[(scalingIndex() + step + ScalingCount) % ScalingCount].Value;
        else if (r == Sharpness)
            s_settings.FsrSharpness = std::clamp(s_settings.FsrSharpness + step, 1, 10);
    };
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & (HidNpadButton_B | HidNpadButton_Plus))
            break;
        if ((down & HidNpadButton_AnyDown) && row + 1 < Rows)
            row++;
        if ((down & HidNpadButton_AnyUp) && row > 0)
            row--;
        bool chosen = (down & HidNpadButton_A) != 0;
        if (down & HidNpadButton_AnyRight)
            change(row, 1);
        if (down & HidNpadButton_AnyLeft)
            change(row, -1);
        float tx, ty;
        bool touch = ui.Touch(tx, ty);
        if (touch && !touching && tx >= MenuX && tx < MenuX + MenuW && ty >= MenuY && ty < MenuY + Rows * RowHeight)
        {
            int r = int((ty - MenuY) / RowHeight);
            if (r == ScalingRow || r == Sharpness)
                change(r, tx < MenuX + MenuW / 2 ? -1 : 1);
            else
                chosen = r == row;
            row = r;
        }
        touching = touch;
        if (chosen && row == Back)
            break;
        if (chosen && row == Leave)
        {
            leave = true;
            break;
        }
        if (chosen)
            change(row, 1);

        player::Renderer& s = ui.Screen;
        s.Begin(0xFF000000);
        drawGame();
        s.Fill({0, 0, 1280, 720}, Rgb(0, 0, 0, 150));
        s.Fill({MenuX - 24, MenuY - 70, MenuW + 48, Rows * RowHeight + 110}, kPanel, 18);
        ui.Text.Draw(Tr("Paused"), MenuX, MenuY - 56, 30, kText);
        const auto& scaling = player::kScalingChoices[scalingIndex()];
        std::string labels[Rows] = {Tr("Back to the game"), Trf("Scaling: {}", Tr(scaling.Label)), Trf("FSR sharpness: {}", s_settings.FsrSharpness),
                                    Tr("Leave the game")};
        for (int r = 0; r < Rows; r++)
        {
            float y = MenuY + float(r) * RowHeight;
            if (r == row)
                s.Fill({MenuX - 12, y, MenuW + 24, RowHeight - 10}, kAccent, 12);
            std::string text = labels[r];
            if (r == ScalingRow || r == Sharpness)
                text = std::string("<  ") + text + "  >";
            ui.Text.Draw(text, MenuX + 8, y + 12, 28, kText);
        }
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = Hints({{kButtonA, "OK"}, {kButtonLeftRight, "Change"}, {kButtonB, "Back to the game"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    if (s_settings.PictureScaling != before.PictureScaling || s_settings.FsrSharpness != before.FsrSharpness)
        s_settings.Save(kSettings);
    return leave;
}

/// The scripts' MessageBoxA over the game's picture (they wait for it, as on Windows), as C#
/// MessageDialog: the caption, the text and the buttons of the type (MB_OK, MB_OKCANCEL,
/// MB_YESNOCANCEL, MB_YESNO; MB_DEFBUTTON2 / 3 chosen first). The answer is the button's Windows
/// number (1 OK, 2 cancel, 6 yes, 7 no); B is cancel, or the last button where there is none.
int MessageDialog(Ui& ui, const std::function<void()>& drawGame, const std::string& text, const std::string& caption, int type)
{
    struct Choice
    {
        const char* Label;
        int Result;
    };
    std::vector<Choice> buttons;
    switch (type & 0xF)
    {
    case 1: buttons = {{"OK", 1}, {"Cancel", 2}}; break;
    case 3: buttons = {{"Yes", 6}, {"No", 7}, {"Cancel", 2}}; break;
    case 4: buttons = {{"Yes", 6}, {"No", 7}}; break;
    default: buttons = {{"OK", 1}}; break;
    }
    int count = int(buttons.size()), cancel = buttons.back().Result;
    for (const auto& b : buttons)
        if (b.Result == 2)
            cancel = 2;
    int chosen = std::min(count - 1, (type >> 8) & 0xF);

    constexpr float TextSize = 26, LineHeight = 38, PanelW = 760, Margin = 32, ButtonW = 150, ButtonH = 56, Gap = 16;
    constexpr size_t MostLines = 11;
    auto lines = WrapText(ui, text, TextSize, PanelW - 2 * Margin);
    if (lines.size() > MostLines)
    {
        lines.resize(MostLines);
        lines.back() += "\xE2\x80\xA6";
    }
    float captionH = caption.empty() ? 0 : 48;
    float panelH = Margin + captionH + float(lines.size()) * LineHeight + 24 + ButtonH + Margin;
    float panelX = (1280 - PanelW) / 2, panelY = std::max(20.0f, (672 - panelH) / 2), buttonsY = panelY + panelH - Margin - ButtonH;
    auto buttonX = [&](int k) { return panelX + PanelW - Margin - float(count - k) * ButtonW - float(count - 1 - k) * Gap; };
    // a button answers when the touch that pressed it is lifted on it
    float tx = 0, ty = 0;
    bool touching = ui.Touch(tx, ty);
    int pressed = -1;
    auto under = [&] {
        for (int k = 0; k < count; k++)
            if (tx >= buttonX(k) && tx < buttonX(k) + ButtonW && ty >= buttonsY && ty < buttonsY + ButtonH)
                return k;
        return -1;
    };
    while (appletMainLoop())
    {
        padUpdate(&ui.Pad);
        u64 down = padGetButtonsDown(&ui.Pad);
        if (down & HidNpadButton_A)
            return buttons[chosen].Result;
        if (down & HidNpadButton_B)
            return cancel;
        if ((down & HidNpadButton_AnyLeft) && chosen > 0)
            chosen--;
        if ((down & HidNpadButton_AnyRight) && chosen + 1 < count)
            chosen++;
        float x, y;
        bool touch = ui.Touch(x, y);
        if (touch)
        {
            tx = x;
            ty = y;
        }
        if (touch && !touching)
        {
            pressed = under();
            if (pressed >= 0)
                chosen = pressed;
        }
        else if (!touch && touching && pressed >= 0 && under() == pressed)
            return buttons[pressed].Result;
        touching = touch;

        player::Renderer& s = ui.Screen;
        s.Begin(0xFF000000);
        drawGame();
        s.Fill({0, 0, 1280, 720}, Rgb(0, 0, 0, 150));
        s.Fill({panelX, panelY, PanelW, panelH}, kPanel, 18);
        if (!caption.empty())
            ui.Text.Draw(ui.Text.Fit(caption, 28, PanelW - 2 * Margin), panelX + Margin, panelY + Margin - 4, 28, kDim);
        for (size_t k = 0; k < lines.size(); k++)
            ui.Text.Draw(lines[k], panelX + Margin, panelY + Margin + captionH + float(k) * LineHeight, TextSize, kText);
        for (int k = 0; k < count; k++)
        {
            float bx = buttonX(k);
            s.Fill({bx, buttonsY, ButtonW, ButtonH}, k == chosen ? kAccent : kBackground, 12);
            float w = ui.Text.Measure(Tr(buttons[k].Label), TextSize);
            ui.Text.Draw(Tr(buttons[k].Label), bx + (ButtonW - w) / 2, buttonsY + 13, TextSize, kText);
        }
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = count == 1 ? Hints({{kButtonA, "OK"}})
                                       : Hints({{kButtonLeftRight, "Choose"}, {kButtonA, "OK"}, {kButtonB, cancel == 2 ? "Cancel" : "No"}});
        ui.Text.Draw(hints, 1280 - 40 - ui.Text.Measure(hints, 24), 682, 24, kText);
        s.End();
    }
    return cancel;
}

/// frameLimit: leave after that many frames (0: when the player leaves; for tests over nxlink).
void Play(Ui& ui, const std::string& path, uint64_t frameLimit = 0)
{
    std::string folder = Files::FileName(path);
    auto scheme = player::SchemeFor(path, kPlayed);
    if (!scheme)
    {
        Log("%s: not recognised (no .WAR archive, or no scheme in Formats.Json opens it)\n", folder.c_str());
        // what is there, to tell which
        for (const char* root : {"sdmc:/", "sdmc:/OpenShiina", kGames})
            for (const auto& name : Files::FolderNames(root))
                Log("  %s/%s/\n", root, name.c_str());
        for (const auto& name : Files::FileNames(path))
            Log("    %s\n", name.c_str());
        return;
    }
    ui.Splash(folder);
    // played now: first in the list from here on, even if it never comes back
    player::RecordPlay(kPlayed, folder, 0);
    LogMemory("before the game");
    auto data = game::GameData::Open(path, scheme);
    auto setup = game::GameSetup::Read(*data, kSaves);
    Log("%s: %s, engine v%s, %dx%d, first script %s\n", folder.c_str(), setup.Game.c_str(), setup.Version.c_str(), setup.Width, setup.Height,
        setup.StartScript.c_str());
    SwitchHost host(*data, setup.SaveFolder);
    auto vm = setup.CreateVm(host, kData);
    // HOME (or the console asleep) stops the game where it is, its clock and its sound too, whatever
    // RIO.INI's Background says (on Windows a game may go on behind other windows)
    vm->SetPauseInBackground(true);
    // The decoded files kept (the setting "File cache")
    vm->FileCacheBytes = s_settings.FileCacheBytes();
    Log("File cache: %lld MB\n", (long long)(vm->FileCacheBytes >> 20));
    // The GPU mode (ui/DekoAccelerator): the heavy pixel routines as compute shaders, on unless
    // sdmc:/OpenShiina/gpu starts with '0'; with gpu-check as well every call of them runs on both
    // and the bytes are compared (the log tells). Without it (it cannot start) the CPU does them
    if (!SwitchOff(kGpuOn))
    {
        try
        {
            if (!s_gpu)
                s_gpu = std::make_unique<player::DekoAccelerator>(ui.Screen.Device());
            vm->Accelerator = s_gpu.get();
            vm->GpuCheck = SwitchOn(kGpuCheck);
        }
        catch (const std::exception& e)
        {
            Log("GPU mode: %s; the CPU draws\n", e.what());
        }
    }
    Log("GPU mode: %s\n", vm->Accelerator ? (vm->GpuCheck ? "on, checked against the CPU" : "on") : "off");
    // With text-check every glyph is drawn both in place and the way before, the bytes compared
    vm->TextCheck = SwitchOn(kTextCheck);
    if (vm->TextCheck)
        Log("Text check: on\n");
    LogMemory("its first script loaded");

    // The sound device: 44.1 kHz stereo floats, the mixer read on SDL's audio thread
    SDL_AudioSpec want{}, have{};
    want.freq = audio::ScnMixer::Rate;
    want.format = AUDIO_F32SYS;
    want.channels = 2;
    want.samples = 1024;
    want.userdata = &host.Mixer();
    want.callback = [](void* mixer, Uint8* stream, int length) {
        static_cast<audio::ScnMixer*>(mixer)->Read(reinterpret_cast<float*>(stream), length / int(2 * sizeof(float)));
    };
    SDL_AudioDeviceID sound = SDL_OpenAudioDevice(nullptr, 0, &want, &have, 0);
    if (sound == 0)
        Log("No sound device: %s\n", SDL_GetError());
    else
        SDL_PauseAudioDevice(sound, 0);

    player::Scaling scaling = s_settings.PictureScaling;
    Rect place = player::Renderer::Placement(scaling, setup.Width, setup.Height);
    LogMemory("the screen made");

    auto toGame = [&](float x, float y) {
        host.MouseX = std::clamp(int((x - place.X) * float(setup.Width) / place.W), 0, setup.Width - 1);
        host.MouseY = std::clamp(int((y - place.Y) * float(setup.Height) / place.H), 0, setup.Height - 1);
    };
    auto mouse = [&](int message, int buttons) {
        host.Buttons = buttons;
        vm->WindowMessage(message, buttons, ScnMessage::Point(host.MouseX, host.MouseY));
    };

    // Every 2 seconds: the frames shown in them and, when they were fewer than 50, where the time
    // of the scripts went (the embedded routines by name, the opcodes)
    std::unordered_map<std::string, ScnVm::RoutineTime> routineTimes;
    std::unordered_map<int, ScnVm::RoutineTime> opTimes;
    vm->RoutineTimes = &routineTimes;
    vm->OpTimes = &opTimes;
    uint64_t frames = 0, started = armGetSystemTick(), slowest = 0, spanStart = started, spanFrames = 0, spanScripts = 0;
    uint64_t pictureVersion = 1, pointerShownUntil = 0;
    bool running = true, focused = true, touching = false;
    // frames until the pointer leaves the picture after a touch lifts (0: it stays)
    int awayIn = 0;
    // The frame rate shown (the setting "Show the frame rate"): frames a second and the slowest
    // frame, once a second
    uint64_t meterStart = started, lastFrame = started, meterSlowest = 0, meterFrames = 0;
    std::string meterText;
    uint64_t playTicks = 0, lastTick = started;
    // after the game stood still (the menu, HOME, a message box): the time it stood is no frame's
    auto restartSpans = [&] {
        spanStart = meterStart = lastFrame = armGetSystemTick();
        spanFrames = spanScripts = meterFrames = meterSlowest = 0;
    };
    // The game's picture: each of the two textures takes it again when it is behind
    auto drawGame = [&] {
        player::Texture& picture = ui.Screen.PictureTexture(setup.Width, setup.Height);
        if (picture.Version != pictureVersion)
        {
            CopyPicture(vm->Window(), setup.Width, setup.Height, picture);
            picture.Version = pictureVersion;
        }
        ui.Screen.Picture(picture, place, scaling, s_settings.FsrSharpness);
    };
    // The scripts' MessageBoxA, over the picture as they have drawn it so far; the time it waits
    // is not the scripts'
    bool asked = false;
    uint64_t askedTicks = 0;
    host.AskPlayer = [&](const std::string& text, const std::string& caption, int type) {
        uint64_t start = armGetSystemTick();
        if (vm->ScreenInvalidated)
        {
            vm->ScreenInvalidated = false;
            pictureVersion++;
        }
        int answer = MessageDialog(ui, drawGame, Text::Cp932ToUtf8(text), Text::Cp932ToUtf8(caption), type);
        asked = true;
        askedTicks += armGetSystemTick() - start;
        return answer;
    };
    try
    {
        while (running && appletMainLoop())
        {
            // the play time: while the game is in front (its menu and message boxes too)
            uint64_t tick = armGetSystemTick();
            if (focused)
                playTicks += tick - lastTick;
            lastTick = tick;
            padUpdate(&ui.Pad);
            u64 down = padGetButtonsDown(&ui.Pad), up = padGetButtonsUp(&ui.Pad);
            if (down & HidNpadButton_Plus)
            {
                // The menu, the game standing still behind it as when HOME is pressed
                vm->Notify(ScnEvent::Deactivate);
                bool leave = GameMenu(ui, drawGame);
                scaling = s_settings.PictureScaling;
                place = player::Renderer::Placement(scaling, setup.Width, setup.Height);
                if (leave)
                    break;
                vm->Notify(ScnEvent::Activate);
                restartSpans();
                continue;
            }
            if (down & HidNpadButton_B)
                mouse(ScnMessage::RButtonDown, host.Buttons | 2);
            if (up & HidNpadButton_B)
                mouse(ScnMessage::RButtonUp, host.Buttons & ~2);
            for (const auto& k : kKeys)
                if ((down | up) & k.Button)
                {
                    bool pressed = (down & k.Button) != 0;
                    host.Keys[k.VirtualKey] = pressed;
                    vm->WindowMessage(pressed ? ScnMessage::KeyDown : ScnMessage::KeyUp, k.VirtualKey, ScnMessage::Key(k.ScanCode, pressed, false));
                }
            // a touch: the mouse there, the left button held
            float tx, ty;
            bool touch = ui.Touch(tx, ty);
            if (touch)
                toGame(tx, ty);
            if (touch && !touching)
                mouse(ScnMessage::LButtonDown, host.Buttons | 1);
            else if (!touch && touching)
            {
                mouse(ScnMessage::LButtonUp, host.Buttons & ~1);
                awayIn = 2;
            }
            // Once the game has had the button up where the finger was, the pointer leaves the
            // picture: a finger is not a mouse that stays, and a button under it would stay lit
            if (touch)
                awayIn = 0;
            else if (awayIn > 0 && --awayIn == 0)
                host.MouseX = host.MouseY = -1;
            touching = touch;
            // the left stick moves the pointer, faster the further it is pushed
            HidAnalogStickState stick = padGetStickPos(&ui.Pad, 0);
            if (std::abs(stick.x) > 6000 || std::abs(stick.y) > 6000)
            {
                host.MouseX = std::clamp(host.MouseX + stick.x / 4096, 0, setup.Width - 1);
                host.MouseY = std::clamp(host.MouseY - stick.y / 4096, 0, setup.Height - 1);
                pointerShownUntil = armGetSystemTick() + armNsToTicks(2000000000ull);
            }

            // Out of focus (HOME, a dialog of the system): the game as a window in the background
            bool inFocus = appletGetFocusState() == AppletFocusState_InFocus;
            if (inFocus != focused)
            {
                focused = inFocus;
                vm->Notify(focused ? ScnEvent::Activate : ScnEvent::Deactivate);
                restartSpans();
            }
            if (!focused)
            {
                svcSleepThread(50000000);
                continue;
            }

            uint64_t before = armGetSystemTick();
            if (frameLimit != 0 && frames >= frameLimit)
                break;
            if (!vm->RunFrame())
            {
                Log("The scripts quit after %llu frames.\n", (unsigned long long)frames);
                break;
            }
            uint64_t scripts = armGetSystemTick() - before - askedTicks;
            askedTicks = 0;
            if (asked)
            {
                // The buttons let go while the game asked: let go for the game too
                asked = false;
                u64 held = padGetButtons(&ui.Pad);
                for (const auto& k : kKeys)
                {
                    bool down = false;
                    for (const auto& other : kKeys)
                        down |= other.VirtualKey == k.VirtualKey && (held & other.Button) != 0;
                    if (host.Keys[k.VirtualKey] && !down)
                    {
                        host.Keys[k.VirtualKey] = false;
                        vm->WindowMessage(ScnMessage::KeyUp, k.VirtualKey, ScnMessage::Key(k.ScanCode, false, false));
                    }
                }
                if ((host.Buttons & 2) && !(held & HidNpadButton_B))
                    mouse(ScnMessage::RButtonUp, host.Buttons & ~2);
                restartSpans();
            }
            for (const auto& check : vm->GpuChecks)
                Log("[gpu] %s\n", check.c_str());
            vm->GpuChecks.clear();
            for (const auto& check : vm->TextChecks)
                Log("[text] %s\n", check.c_str());
            vm->TextChecks.clear();
            slowest = std::max(slowest, scripts);
            spanScripts += scripts;
            frames++;
            spanFrames++;
            if (vm->ScreenInvalidated)
            {
                vm->ScreenInvalidated = false;
                pictureVersion++;
            }

            ui.Screen.Begin(0xFF000000);
            drawGame();
            if (armGetSystemTick() < pointerShownUntil)
            {
                float px = place.X + (float(host.MouseX) + 0.5f) * place.W / float(setup.Width);
                float py = place.Y + (float(host.MouseY) + 0.5f) * place.H / float(setup.Height);
                ui.Screen.Fill({px - 9, py - 9, 18, 18}, Rgb(0, 0, 0, 160), 9);
                ui.Screen.Fill({px - 6, py - 6, 12, 12}, kText, 6);
            }
            if (s_settings.ShowFrameRate && !meterText.empty())
            {
                float w = ui.Text.Measure(meterText, 20);
                ui.Screen.Fill({8, 8, w + 20, 32}, Rgb(0, 0, 0, 128), 6);
                ui.Text.Draw(meterText, 18, 13, 20, Rgb(255, 255, 255));
            }
            ui.Screen.End();

            uint64_t now = armGetSystemTick();
            meterSlowest = std::max(meterSlowest, now - lastFrame);
            lastFrame = now;
            meterFrames++;
            if (armTicksToNs(now - meterStart) >= 1000000000ull)
            {
                double seconds = double(armTicksToNs(now - meterStart)) / 1e9;
                meterText = Trf("{:.0f} fps, slowest {:.1f} ms", double(meterFrames) / seconds, double(armTicksToNs(meterSlowest)) / 1e6);
                meterStart = now;
                meterFrames = meterSlowest = 0;
            }
            if (armTicksToNs(now - spanStart) >= 2000000000ull)
            {
                double seconds = double(armTicksToNs(now - spanStart)) / 1e9;
                if (spanFrames / seconds < 50)
                {
                    std::vector<std::pair<std::string, ScnVm::RoutineTime>> top(routineTimes.begin(), routineTimes.end());
                    std::sort(top.begin(), top.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
                    std::string names;
                    for (size_t k = 0; k < top.size() && k < 7; k++)
                        names += std::format(", {} {:.0f} ms ({} calls)", top[k].first, top[k].second.Milliseconds, top[k].second.Calls);
                    std::vector<std::pair<int, ScnVm::RoutineTime>> ops(opTimes.begin(), opTimes.end());
                    std::sort(ops.begin(), ops.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
                    names += "; opcodes";
                    for (size_t k = 0; k < ops.size() && k < 5; k++)
                        names += std::format(" {:04X} {:.0f} ms ({})", ops[k].first, ops[k].second.Milliseconds, ops[k].second.Calls);
                    Log("frame %llu: %.1f a second, scripts %.0f ms of %.1f s, heap %zu MB%s\n", (unsigned long long)frames,
                        spanFrames / seconds, double(armTicksToNs(spanScripts)) / 1e6, seconds, HeapInUse() >> 20, names.c_str());
                }
                routineTimes.clear();
                opTimes.clear();
                spanStart = now;
                spanFrames = spanScripts = 0;
            }
            if (frames % 600 == 0)
            {
                double seconds = double(armTicksToNs(armGetSystemTick() - started)) / 1e9;
                Log("%llu frames, %.1f a second, the slowest frame's scripts %.1f ms\n", (unsigned long long)frames, frames / seconds,
                    double(armTicksToNs(slowest)) / 1e6);
                slowest = 0;
            }
        }
    }
    catch (const ScnException& e)
    {
        Log("Script error at frame %llu: %s\n  at %s, opcode %04X\n", (unsigned long long)frames, e.what(), vm->DescribeAddress(e.Address).c_str(), e.Op);
    }
    catch (const std::bad_alloc& e)
    {
        // (what() tells when the scripts' memory could not be mapped; the size is that of the last
        // operator new that failed, which may be one a library tried and did without)
        Log("Out of memory at frame %llu: %s (the last allocation that failed: %zu bytes)\n", (unsigned long long)frames, e.what(),
            s_failedSize);
        LogMemory("then");
    }
    catch (const std::exception& e)
    {
        Log("Error at frame %llu: %s\n", (unsigned long long)frames, e.what());
    }
    if (vm->TextCheck)
        Log("[text] %lld glyphs compared, %lld differed\n", (long long)vm->TextCheckGlyphs, (long long)vm->TextCheckDiffered);
    for (const auto& [routine, c] : vm->GpuCheckCounts)
        Log("[gpu] %s: %lld calls compared, %lld differed; %.2f ms on the GPU against %.2f on the CPU a 100,000 pixels\n", routine.c_str(),
            (long long)c.Calls, (long long)c.Differed, c.GpuMs * 100000 / double(std::max<int64_t>(c.Pixels, 1)),
            c.CpuMs * 100000 / double(std::max<int64_t>(c.Pixels, 1)));
    player::RecordPlay(kPlayed, folder, int64_t(armTicksToNs(playTicks) / 1000000000ull));
    if (sound != 0)
        SDL_CloseAudioDevice(sound);
    Log("[leave] the sound closed\n");
    // (each step of leaving the game in the log, for a leaving that never ends)
    uint64_t leaving = armGetSystemTick();
    vm.reset();
    Log("[leave] the interpreter let go (%.0f ms)\n", double(armTicksToNs(armGetSystemTick() - leaving)) / 1e6);
}

} // namespace

// Where the program's code starts (the .nro's or the title's), which addresses in the log count
// from: aarch64-none-elf-addr2line -f -C -e player.elf <offsets>
uintptr_t CodeStart()
{
    static const uintptr_t start = [] {
        MemoryInfo info{};
        u32 page = 0;
        svcQueryMemory(&info, &page, reinterpret_cast<uintptr_t>(&CodeStart));
        return uintptr_t(info.addr);
    }();
    return start;
}

// Allocations through malloc, the size of one that fails kept for the log; one of 1 GB or more
// logs where it was made from (a size gone wrong, mostly)
void* operator new(size_t size)
{
    if (size >= (size_t(1) << 30))
    {
        struct Trace
        {
            std::string text;
            int frames = 0;
        } trace;
        _Unwind_Backtrace(
            [](_Unwind_Context* context, void* argument) {
                auto* t = static_cast<Trace*>(argument);
                char offset[24];
                std::snprintf(offset, sizeof offset, " 0x%lx", (unsigned long)(_Unwind_GetIP(context) - CodeStart()));
                t->text += offset;
                return ++t->frames < 16 ? _URC_NO_REASON : _URC_END_OF_STACK;
            },
            &trace);
        Log("An allocation of %zu bytes, from%s\n", size, trace.text.c_str());
    }
    if (void* p = std::malloc(size ? size : 1))
        return p;
    s_failedSize = size;
    throw std::bad_alloc();
}

// (operator new above takes its memory from malloc: GCC cannot see that freeing it is right)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wmismatched-new-delete"
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
#pragma GCC diagnostic pop

extern "C" void userAppInit()
{
    devhost::ReturnAtExit();
    // (nxlink and devhost start the player only through a loader: installed as a title of its
    // own, it has no sockets to open)
    if (!envIsNso() && R_SUCCEEDED(socketInitializeDefault()))
        s_nxlink = nxlinkConnectToHost(false, true);
}

extern "C" void userAppExit()
{
    if (s_nxlink >= 0)
        close(s_nxlink);
    if (!envIsNso())
        socketExit();
}

int main(int argc, char** argv)
{
    // What the scripts' memory needs of the system, first, while the process has taken little
    std::string prepared;
    try
    {
        scripting::ScnAddressSpace::Prepare();
    }
    catch (const std::exception& e)
    {
        prepared = e.what();
    }
    romfsInit();
    Files::CreateDirectories(kSaves);
    // (where the games go, there to be seen on the card; the console's storage is written only by a move)
    Files::CreateDirectories(kGames);
    s_settings = player::Settings::Load(kSettings);
    if (kLanguages)
        player::SetLanguage(s_settings.Language);
    // The logs of the last runs kept (player.1.log the run before this one, up to player.3.log): the
    // OpenShiina title starts the player before hbmenu, so a new log must not take the one to read
    // (the SD card's file system moves no file onto another: the oldest goes first)
    {
        const std::string base = std::string(kLog).substr(0, std::string(kLog).size() - 4);
        auto kept = [&](int k) { return base + "." + std::to_string(k) + ".log"; };
        unlink(kept(3).c_str());
        rename(kept(2).c_str(), kept(3).c_str());
        rename(kept(1).c_str(), kept(2).c_str());
        rename(kLog, kept(1).c_str());
    }
    s_log = Files::Open(kLog, "wb");
    if (!prepared.empty())
        Log("At the start: %s\n", prepared.c_str());
    std::unique_ptr<Ui> ui;
    try
    {
        // the logo first, then the formats (a moment to read)
        ui = std::make_unique<Ui>();
        ui->Splash();
        for (const auto& font : ui->Text.ExtraFonts())
            Log("Font: %s\n", font.c_str());
        // (the Switch has no Vietnamese: whether its fonts draw all the letters of the screens' words)
        if (kLanguages)
        {
            std::string missing;
            for (char32_t c : ui->Text.Missing(player::VietnameseWords()))
                missing += std::format(" U+{:04X}", uint32_t(c));
            Log("Vietnamese: %s\n", missing.empty() ? "every letter drawn" : ("no font draws" + missing).c_str());
        }
        // (the NEON lanes of the pixel routines, which the PC tests do not run)
        std::string lanes = ScnVm::CheckVectorLanes();
        Log("Vector lanes: %s\n", lanes.empty() ? "as the x86 code" : lanes.c_str());
        std::string movieLanes = formats::Vc1Video::CheckVectorLanes();
        Log("Movie lanes: %s\n", movieLanes.empty() ? "as the scalar code" : movieLanes.c_str());
    }
    catch (const std::exception& e)
    {
        Log("The screen: %s\n", e.what());
    }
    archives::FormatManager::SetInstance(std::make_unique<archives::FormatManager>(Files::Combine(kData, "Formats.Json")));
    for (const auto& error : archives::FormatManager::Instance()->LoadErrors())
        Log("%s\n", error.c_str());
    if (SDL_Init(SDL_INIT_AUDIO) != 0)
        Log("SDL audio: %s\n", SDL_GetError());

    // A game folder named on the command line (nxlink ... OpenShiina.nro <folder>) starts at once;
    // else the list of games, back to it after each one (the title on the HOME menu starts here)
    std::string folder;
    // (and a number after it: leave after that many frames, for tests)
    uint64_t frameLimit = 0;
    for (int k = 1; k < argc; k++)
        if (argv[k] && argv[k][0] != '_')
        {
            if (folder.empty())
                folder = argv[k];
            else if (frameLimit == 0)
                frameLimit = std::strtoull(argv[k], nullptr, 10);
        }
    bool named = !folder.empty();
    if (named)
        folder = Files::Combine(kGames, folder);
    // The console's own storage, for games put there over DBI's MTP ("Nand USER") or moved there (R)
    FsFileSystem userFs;
    bool internal = R_SUCCEEDED(fsOpenBisFileSystem(&userFs, FsBisPartitionId_User, ""));
    if (internal && fsdevMountDevice("user", userFs) < 0)
    {
        fsFsClose(&userFs);
        internal = false;
    }
    while (ui)
    {
        if (!named)
            folder = PickGame(*ui, internal);
        if (folder == kToHbmenu)
        {
            envSetNextLoad("sdmc:/hbmenu.nro", "sdmc:/hbmenu.nro");
            break;
        }
        if (folder.empty())
            break;
        try
        {
            Play(*ui, folder, frameLimit);
        }
        catch (const std::exception& e)
        {
            Log("%s: %s\n", folder.c_str(), e.what());
        }
        if (named)
            break;
    }
    // the GPU mode before the screen it runs on
    s_gpu.reset();
    ui.reset();
    if (internal)
        fsdevUnmountDevice("user");
    SDL_Quit();
    if (s_log)
        std::fclose(s_log);
    romfsExit();
    return 0;
}
