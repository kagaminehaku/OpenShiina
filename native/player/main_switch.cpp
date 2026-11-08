// OpenShiina.nro: the player on the Switch. It shows its logo at once (ui/Renderer, deko3d), then the
// game folders of sdmc:/OpenShiina/Games to pick from (one named as the first argument starts at
// once); the game's picture fills the screen as the setting "Scaling" says (Sharp for now). Its
// saves go to sdmc:/OpenShiina/Saves/<game>; Formats.Json, the opcode tables, the shaders and the
// logo are in the romfs. What it prints goes to sdmc:/OpenShiina/player.log and, started by
// nxlink -s, to the PC.
//
// Input, as the C# player's touch bar and joystick give it: a touch is the mouse (moved there, the
// left button held while touching); A is Return, B the right button, X Escape, Y Space, R / ZR
// Control (held: skip), L Page Up, the D-pad the arrow keys, the left stick moves the pointer (shown
// while it moves); + leaves the game. Text is drawn with the Switch's own Japanese font
// (FreeTypeFonts); the sound goes out through SDL (only its audio is used).
#include <SDL.h>
#include <switch.h>
#include <unistd.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <format>
#include <memory>
#include <new>
#include <string>
#include <vector>

#include "DevHostReturn.h"
#include "FreeTypeFonts.h"
#include "archives/FormatManager.h"
#include "audio/ScnMixer.h"
#include "audio/ScnMusic.h"
#include "audio/ScnSound.h"
#include "game/GameData.h"
#include "game/GameSetup.h"
#include "platform/DibShapes.h"
#include "platform/Files.h"
#include "scripting/ScnVm.h"
#include "text/Text.h"
#include "ui/Renderer.h"
#include "ui/UiText.h"

using namespace openshiina;
using namespace openshiina::scripting;
using player::Rect;

namespace {

constexpr const char* kGames = "sdmc:/OpenShiina/Games";
constexpr const char* kSaves = "sdmc:/OpenShiina/Saves";
constexpr const char* kLog = "sdmc:/OpenShiina/player.log";
constexpr const char* kData = "romfs:/Data";

int s_nxlink = -1;
std::FILE* s_log = nullptr;

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
void LogMemory(const char* when)
{
    u64 total = 0, used = 0;
    svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    Log("[memory] %s: %llu MB used of %llu MB\n", when, (unsigned long long)(used >> 20), (unsigned long long)(total >> 20));
}

/// A colour as the Renderer takes it, 0xAABBGGRR.
constexpr uint32_t Rgb(uint32_t r, uint32_t g, uint32_t b, uint32_t a = 255) { return a << 24 | b << 16 | g << 8 | r; }

// The logo's colours (C# PlayerTheme's Shiina theme)
constexpr uint32_t kBackground = Rgb(26, 10, 16), kPanel = Rgb(44, 20, 28), kAccent = Rgb(200, 30, 58), kText = Rgb(242, 230, 201),
                   kDim = Rgb(170, 150, 140);

// The buttons' symbols of the Switch's font (Nintendo's extension)
constexpr const char* kButtonA = "\xEE\x83\xA0";
constexpr const char* kButtonPlus = "\xEE\x83\xAF";
constexpr const char* kButtonMinus = "\xEE\x83\xB0";

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

    int MessageBox(const std::string& text, const std::string&, int type) override
    {
        Log("[message box %d] %s\n", type, Text::Cp932ToUtf8(text).c_str());
        return (type & 0xF) == 4 || (type & 0xF) == 3 ? 6 : 1;    // yes / OK
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

/// The game folders, Latin names only (the Switch does not see folders with Japanese names written
/// from Windows): A plays the one chosen (a tap chooses, a tap on the chosen one plays), - opens
/// hbmenu, + leaves.
std::string PickGame(Ui& ui)
{
    auto folders = Files::FolderNames(kGames);
    std::sort(folders.begin(), folders.end());
    constexpr float RowHeight = 60, ListX = 380, ListY = 110, ListW = 860;
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
        if (!folders.empty() && (down & HidNpadButton_A))
            return folders[choice];
        if ((down & (HidNpadButton_AnyDown)) && choice + 1 < folders.size())
            choice++;
        if ((down & (HidNpadButton_AnyUp)) && choice > 0)
            choice--;
        float tx, ty;
        bool touch = ui.Touch(tx, ty);
        if (touch && !touching && tx >= ListX && tx < ListX + ListW && ty >= ListY)
        {
            size_t row = top + size_t((ty - ListY) / RowHeight);
            if (row < folders.size() && row < top + Rows)
            {
                if (row == choice)
                    return folders[choice];
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
        // the rail: the logo, the wordmark
        s.Fill({0, 0, 340, 720}, kPanel);
        s.Image(ui.Logo, {90, 60, 160, 160});
        float ww = 280, wh = ui.Wordmark.Valid() ? ww * float(ui.Wordmark.Height) / float(ui.Wordmark.Width) : 0;
        s.Image(ui.Wordmark, {30, 240, ww, wh});
        // the games
        ui.Text.Draw("Games", ListX, 48, 34, kText);
        if (folders.empty())
            ui.Text.Draw(std::string("No game folder in ") + kGames, ListX, ListY + 10, 24, kDim);
        for (size_t k = top; k < folders.size() && k < top + Rows; k++)
        {
            float y = ListY + float(k - top) * RowHeight;
            if (k == choice)
                s.Fill({ListX - 12, y, ListW, RowHeight - 8}, kAccent, 12);
            ui.Text.Draw(ui.Text.Fit(folders[k], 28, ListW - 40), ListX + 8, y + 10, 28, kText);
        }
        // the buttons
        s.Fill({0, 672, 1280, 48}, kPanel);
        std::string hints = std::string(kButtonA) + " Play     " + kButtonMinus + " hbmenu     " + kButtonPlus + " Quit";
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

void Play(Ui& ui, const std::string& folder)
{
    std::string path = Files::Combine(kGames, folder);
    auto archive = game::GameData::FindArchive(path);
    auto scheme = archive ? archives::FormatManager::Instance()->SchemeForArchive(*archive) : nullptr;
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

    player::Scaling scaling = player::Scaling::Sharp;
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
    try
    {
        while (running && appletMainLoop())
        {
            padUpdate(&ui.Pad);
            u64 down = padGetButtonsDown(&ui.Pad), up = padGetButtonsUp(&ui.Pad);
            if (down & HidNpadButton_Plus)
                running = false;
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
                mouse(ScnMessage::LButtonUp, host.Buttons & ~1);
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
                spanStart = armGetSystemTick();
                spanFrames = spanScripts = 0;
            }
            if (!focused)
            {
                svcSleepThread(50000000);
                continue;
            }

            uint64_t before = armGetSystemTick();
            if (!vm->RunFrame())
            {
                Log("The scripts quit after %llu frames.\n", (unsigned long long)frames);
                break;
            }
            uint64_t scripts = armGetSystemTick() - before;
            slowest = std::max(slowest, scripts);
            spanScripts += scripts;
            frames++;
            spanFrames++;
            if (vm->ScreenInvalidated)
            {
                vm->ScreenInvalidated = false;
                pictureVersion++;
            }

            // The picture: each of the two textures takes it again when it is behind
            ui.Screen.Begin(0xFF000000);
            player::Texture& picture = ui.Screen.PictureTexture(setup.Width, setup.Height);
            if (picture.Version != pictureVersion)
            {
                CopyPicture(vm->Window(), setup.Width, setup.Height, picture);
                picture.Version = pictureVersion;
            }
            ui.Screen.Picture(picture, place, scaling);
            if (armGetSystemTick() < pointerShownUntil)
            {
                float px = place.X + (float(host.MouseX) + 0.5f) * place.W / float(setup.Width);
                float py = place.Y + (float(host.MouseY) + 0.5f) * place.H / float(setup.Height);
                ui.Screen.Fill({px - 9, py - 9, 18, 18}, Rgb(0, 0, 0, 160), 9);
                ui.Screen.Fill({px - 6, py - 6, 12, 12}, kText, 6);
            }
            ui.Screen.End();

            uint64_t now = armGetSystemTick();
            if (armTicksToNs(now - spanStart) >= 2000000000ull)
            {
                double seconds = double(armTicksToNs(now - spanStart)) / 1e9;
                if (spanFrames / seconds < 50)
                {
                    std::vector<std::pair<std::string, ScnVm::RoutineTime>> top(routineTimes.begin(), routineTimes.end());
                    std::sort(top.begin(), top.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
                    std::string names;
                    for (size_t k = 0; k < top.size() && k < 4; k++)
                        names += std::format(", {} {:.0f} ms ({} calls)", top[k].first, top[k].second.Milliseconds, top[k].second.Calls);
                    std::vector<std::pair<int, ScnVm::RoutineTime>> ops(opTimes.begin(), opTimes.end());
                    std::sort(ops.begin(), ops.end(), [](const auto& a, const auto& b) { return a.second.Milliseconds > b.second.Milliseconds; });
                    names += "; opcodes";
                    for (size_t k = 0; k < ops.size() && k < 5; k++)
                        names += std::format(" {:04X} {:.0f} ms ({})", ops[k].first, ops[k].second.Milliseconds, ops[k].second.Calls);
                    Log("frame %llu: %.1f a second, scripts %.0f ms of %.1f s%s\n", (unsigned long long)frames, spanFrames / seconds,
                        double(armTicksToNs(spanScripts)) / 1e6, seconds, names.c_str());
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
    catch (const std::bad_alloc&)
    {
        Log("Out of memory at frame %llu (an allocation of %zu bytes)\n", (unsigned long long)frames, s_failedSize);
        LogMemory("then");
    }
    catch (const std::exception& e)
    {
        Log("Error at frame %llu: %s\n", (unsigned long long)frames, e.what());
    }
    if (sound != 0)
        SDL_CloseAudioDevice(sound);
}

} // namespace

// Allocations through malloc, the size of one that fails kept for the log
void* operator new(size_t size)
{
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
    if (R_SUCCEEDED(socketInitializeDefault()))
        s_nxlink = nxlinkConnectToHost(false, true);
}

extern "C" void userAppExit()
{
    if (s_nxlink >= 0)
        close(s_nxlink);
    socketExit();
}

int main(int argc, char** argv)
{
    romfsInit();
    Files::CreateDirectories(kSaves);
    s_log = Files::Open(kLog, "wb");
    std::unique_ptr<Ui> ui;
    try
    {
        // the logo first, then the formats (a moment to read)
        ui = std::make_unique<Ui>();
        ui->Splash();
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
    for (int k = 1; k < argc && folder.empty(); k++)
        if (argv[k] && argv[k][0] != '_')
            folder = argv[k];
    bool named = !folder.empty();
    while (ui)
    {
        if (!named)
            folder = PickGame(*ui);
        if (folder == kToHbmenu)
        {
            envSetNextLoad("sdmc:/hbmenu.nro", "sdmc:/hbmenu.nro");
            break;
        }
        if (folder.empty())
            break;
        try
        {
            Play(*ui, folder);
        }
        catch (const std::exception& e)
        {
            Log("%s: %s\n", folder.c_str(), e.what());
        }
        if (named)
            break;
    }
    ui.reset();
    SDL_Quit();
    if (s_log)
        std::fclose(s_log);
    romfsExit();
    return 0;
}
