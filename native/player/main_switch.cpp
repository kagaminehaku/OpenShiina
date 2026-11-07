// OpenShiina.nro: the player on the Switch. A game folder of sdmc:/OpenShiina/Games is picked on the
// console (one alone, or one named as the first argument, starts at once), then it runs in an SDL2 window of the whole screen, its picture
// scaled to fit. Its saves go to sdmc:/OpenShiina/Saves/<game>; Formats.Json and the opcode tables
// are in the romfs. What it prints goes to sdmc:/OpenShiina/player.log and, started by nxlink -s, to
// the PC.
//
// Input, as the C# player's touch bar and joystick give it: a touch is the mouse (moved there, the
// left button held while touching); A is Return, B the right button, X Escape, Y Space, R / ZR
// Control (held: skip), L Page Up, the D-pad the arrow keys, the left stick moves the pointer;
// + leaves the game. Text is drawn with the Switch's own Japanese font (FreeTypeFonts). Not here yet:
// sound and music, movies.
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

using namespace openshiina;
using namespace openshiina::scripting;

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
/// game's: started over a game - R held - it has all of it).
void LogMemory(const char* when)
{
    u64 total = 0, used = 0;
    svcGetInfo(&total, InfoType_TotalMemorySize, CUR_PROCESS_HANDLE, 0);
    svcGetInfo(&used, InfoType_UsedMemorySize, CUR_PROCESS_HANDLE, 0);
    Log("[memory] %s: %llu MB used of %llu MB\n", when, (unsigned long long)(used >> 20), (unsigned long long)(total >> 20));
}

// SDL2's Switch joystick: the buttons by index
enum Button { A = 0, B = 1, X = 2, Y = 3, L = 6, R = 7, ZL = 8, ZR = 9, Plus = 10, Minus = 11, Left = 12, Up = 13, Right = 14, Down = 15 };

/// A button as a key: its virtual key and scan code.
struct KeyOf
{
    int Button, VirtualKey, ScanCode;
};

constexpr KeyOf kKeys[] = {
    {A, 0x0D, 0x1C},    {X, 0x1B, 0x01},     {Y, 0x20, 0x39},    {R, 0x11, 0x1D},   {ZR, 0x11, 0x1D},
    {L, 0x21, 0x49},    {Left, 0x25, 0x4B},  {Up, 0x26, 0x48},   {Right, 0x27, 0x4D}, {Down, 0x28, 0x50},
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

/// The game folders, Latin names only (the Switch does not see folders with Japanese names written from Windows).
std::string PickGame()
{
    auto folders = Files::FolderNames(kGames);
    std::sort(folders.begin(), folders.end());
    consoleInit(nullptr);
    PadState pad;
    padConfigureInput(1, HidNpadStyleSet_NpadStandard);
    padInitializeDefault(&pad);
    size_t choice = 0;
    std::string picked;
    while (appletMainLoop())
    {
        std::printf("\x1b[1;1H\x1b[2JOpenShiina - pick a game (A starts, - opens hbmenu, + leaves)\n\n");
        if (folders.empty())
            std::printf("No game folder in %s\n", kGames);
        for (size_t k = 0; k < folders.size(); k++)
            std::printf("%s %s\n", k == choice ? ">" : " ", folders[k].c_str());
        consoleUpdate(nullptr);
        padUpdate(&pad);
        u64 down = padGetButtonsDown(&pad);
        if (down & HidNpadButton_Plus)
            break;
        if ((down & HidNpadButton_Minus) && envHasNextLoad())
        {
            picked = kToHbmenu;
            break;
        }
        if (!folders.empty() && (down & HidNpadButton_A))
        {
            picked = folders[choice];
            break;
        }
        if ((down & HidNpadButton_AnyDown) && choice + 1 < folders.size())
            choice++;
        if ((down & HidNpadButton_AnyUp) && choice > 0)
            choice--;
    }
    consoleExit(nullptr);
    return picked;
}

/// Where the game's picture goes on the screen: as large as it fits, its shape kept.
SDL_Rect Fit(int width, int height, int screenWidth, int screenHeight)
{
    int w = screenWidth, h = int(int64_t(screenWidth) * height / width);
    if (h > screenHeight)
    {
        h = screenHeight;
        w = int(int64_t(screenHeight) * width / height);
    }
    return {(screenWidth - w) / 2, (screenHeight - h) / 2, w, h};
}

void Play(const std::string& folder)
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

    SDL_Window* window = SDL_CreateWindow("OpenShiina", 0, 0, 1280, 720, 0);
    SDL_Renderer* renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);
    SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "linear");
    SDL_Texture* picture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_BGR24, SDL_TEXTUREACCESS_STREAMING, setup.Width, setup.Height);
    SDL_Joystick* joystick = SDL_JoystickOpen(0);
    SDL_Rect place = Fit(setup.Width, setup.Height, 1280, 720);
    LogMemory("the window made");

    auto toGame = [&](float fx, float fy) {
        int x = int((fx * 1280 - place.x) * setup.Width / place.w), y = int((fy * 720 - place.y) * setup.Height / place.h);
        host.MouseX = std::clamp(x, 0, setup.Width - 1);
        host.MouseY = std::clamp(y, 0, setup.Height - 1);
    };
    auto mouse = [&](int message, int buttons) {
        host.Buttons = buttons;
        vm->WindowMessage(message, buttons, ScnMessage::Point(host.MouseX, host.MouseY));
    };

    // Every 2 seconds: the frames shown in them and, when they were fewer than 50, where the time
    // of the scripts went (the embedded routines by name)
    std::unordered_map<std::string, ScnVm::RoutineTime> routineTimes;
    std::unordered_map<int, ScnVm::RoutineTime> opTimes;
    vm->RoutineTimes = &routineTimes;
    vm->OpTimes = &opTimes;
    uint64_t frames = 0, started = armGetSystemTick(), slowest = 0, spanStart = started, spanFrames = 0, spanScripts = 0;
    bool running = true, focused = true;
    try
    {
        while (running && appletMainLoop())
        {
            SDL_Event e;
            while (SDL_PollEvent(&e))
            {
                switch (e.type)
                {
                case SDL_QUIT:
                    running = false;
                    break;
                case SDL_FINGERDOWN:
                    toGame(e.tfinger.x, e.tfinger.y);
                    mouse(ScnMessage::LButtonDown, host.Buttons | 1);
                    break;
                case SDL_FINGERMOTION:
                    toGame(e.tfinger.x, e.tfinger.y);
                    break;
                case SDL_FINGERUP:
                    toGame(e.tfinger.x, e.tfinger.y);
                    mouse(ScnMessage::LButtonUp, host.Buttons & ~1);
                    break;
                case SDL_JOYBUTTONDOWN:
                case SDL_JOYBUTTONUP:
                {
                    bool down = e.type == SDL_JOYBUTTONDOWN;
                    int button = e.jbutton.button;
                    if (button == Plus && down)
                        running = false;
                    else if (button == B)
                        mouse(down ? ScnMessage::RButtonDown : ScnMessage::RButtonUp, down ? host.Buttons | 2 : host.Buttons & ~2);
                    for (const auto& k : kKeys)
                        if (k.Button == button)
                        {
                            host.Keys[k.VirtualKey] = down;
                            vm->WindowMessage(down ? ScnMessage::KeyDown : ScnMessage::KeyUp, k.VirtualKey, ScnMessage::Key(k.ScanCode, down, false));
                        }
                    break;
                }
                default:
                    break;
                }
            }
            // The left stick moves the pointer, faster the further it is pushed
            if (joystick)
            {
                int ax = SDL_JoystickGetAxis(joystick, 0), ay = SDL_JoystickGetAxis(joystick, 1);
                if (std::abs(ax) > 6000 || std::abs(ay) > 6000)
                {
                    host.MouseX = std::clamp(host.MouseX + ax / 4096, 0, setup.Width - 1);
                    host.MouseY = std::clamp(host.MouseY + ay / 4096, 0, setup.Height - 1);
                }
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
                SDL_UpdateTexture(picture, nullptr, vm->Window().data(), setup.Width * 3);
            }
            SDL_RenderClear(renderer);
            SDL_RenderCopy(renderer, picture, nullptr, &place);
            SDL_RenderPresent(renderer);
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
    if (joystick)
        SDL_JoystickClose(joystick);
    SDL_DestroyTexture(picture);
    SDL_DestroyRenderer(renderer);
    SDL_DestroyWindow(window);
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

void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }

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
    archives::FormatManager::SetInstance(std::make_unique<archives::FormatManager>(Files::Combine(kData, "Formats.Json")));
    for (const auto& error : archives::FormatManager::Instance()->LoadErrors())
        Log("%s\n", error.c_str());

    // A game folder named on the command line (nxlink ... OpenShiina.nro <folder>) starts at once
    std::string folder;
    for (int k = 1; k < argc && folder.empty(); k++)
        if (argv[k] && argv[k][0] != '_')
            folder = argv[k];
    // Else the list of games, back to it after each one (the title on the HOME menu starts here)
    bool named = !folder.empty();
    for (;;)
    {
        if (!named)
            folder = PickGame();
        if (folder == kToHbmenu)
        {
            envSetNextLoad("sdmc:/hbmenu.nro", "sdmc:/hbmenu.nro");
            break;
        }
        if (folder.empty() || SDL_Init(SDL_INIT_VIDEO | SDL_INIT_JOYSTICK | SDL_INIT_AUDIO) != 0)
            break;
        try
        {
            Play(folder);
        }
        catch (const std::exception& e)
        {
            Log("%s: %s\n", folder.c_str(), e.what());
        }
        SDL_Quit();
        if (named)
            break;
    }
    if (s_log)
        std::fclose(s_log);
    romfsExit();
    return 0;
}
