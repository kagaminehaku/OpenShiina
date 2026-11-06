// The interpreter's types (C# Scripting/ScnVm.cs): what it needs from the engine around it, a
// slot, an operand, a decoded instruction, a script error.
//
// Text is kept as the scripts have it, Shift-JIS bytes, where the C# player decodes it into its
// strings: names, file names and messages cross IScnHost so, and the host decodes what it shows.
#pragma once

#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#include "io/Binary.h"

namespace openshiina::scripting {

class ScnVm;
class IScnFonts;
class IScnShapes;
class IScnSound;
class IScnMusic;

/// A joystick's position (X and Y from 0 to 65535, 32767 in the middle) and buttons (bit 0 = button 1).
struct ScnJoystick
{
    int X = 0, Y = 0, Buttons = 0;
};

/// What the interpreter needs from the engine around it.
class IScnHost
{
public:
    virtual ~IScnHost() = default;

    /// A file by the engine's rules (archives of the game, then its folder); false when missing.
    virtual bool ReadFile(const std::string& name, Bytes& data) = 0;

    /// The length ReadFile gives for a file of the archives, without reading it; none when it is not in them.
    virtual std::optional<int64_t> ArchiveFileSize(const std::string& name)
    {
        Bytes data;
        return ReadFile(name, data) ? std::optional<int64_t>(int64_t(data.size())) : std::nullopt;
    }

    /// The length of a file of one archive of the game (its file name, "BITCHES3_PA.WAR"), without
    /// reading it; none when that archive is missing or does not have the file. Without a better
    /// answer: any archive's file, when that archive is in the game folder.
    virtual std::optional<int64_t> ArchiveFileSize(const std::string& archive, const std::string& name)
    {
        return LooseFileSize(archive) ? ArchiveFileSize(name) : std::nullopt;
    }

    /// The scripts mount (00DD) or unmount (00DF) an archive: a file in several archives then
    /// comes from a mounted one. True when that changed anything.
    virtual bool MountArchive(const std::string&, bool) { return false; }

    /// The game's executable as it shipped, from a copy the game keeps in its archives under
    /// another name, for START's comparison of its executable with that copy; false when there is
    /// none. `name` is the executable in the game folder.
    virtual bool ExecutableCopy(const std::string&, Bytes&) { return false; }

    /// A file of the game folder itself (RIO.INI, movies); false when missing.
    virtual bool ReadLooseFile(const std::string& name, Bytes& data) = 0;

    /// The length ReadLooseFile gives, without reading the file; none when it is missing.
    virtual std::optional<int64_t> LooseFileSize(const std::string& name)
    {
        Bytes data;
        return ReadLooseFile(name, data) ? std::optional<int64_t>(int64_t(data.size())) : std::nullopt;
    }

    /// Milliseconds since some fixed point (timeGetTime).
    virtual uint32_t Milliseconds() = 0;

    /// The window title (SetWindowText).
    virtual void SetTitle(const std::string& title) = 0;

    virtual void SetFullScreen(bool fullScreen) = 0;

    /// Where this game's save data goes (an absolute folder path, UTF-8; the scripts' DataPath).
    virtual std::string SaveFolder() = 0;

    virtual bool ReadSaveFile(const std::string& name, Bytes& data) = 0;
    virtual void WriteSaveFile(const std::string& name, const Bytes& data) = 0;
    virtual void DeleteSaveFile(const std::string& name) = 0;

    /// A message box; returns the button as Windows numbers it (1 OK, 2 cancel, 6 yes, 7 no).
    virtual int MessageBox(const std::string& text, const std::string& caption, int type) = 0;

    /// The font engine text is drawn with, or null to measure and draw nothing.
    virtual IScnFonts* Fonts() { return nullptr; }

    /// The shapes GDI calls of the scripts draw (op_01A4), or null for close approximations.
    virtual IScnShapes* Shapes() { return nullptr; }

    /// The sound buffers, or null to keep sounds silent.
    virtual IScnSound* Sound() { return nullptr; }

    /// The music streams, or null to keep music silent.
    virtual IScnMusic* Music() { return nullptr; }

    /// Every sound (buffers, music, movies) stands still, or goes on where it was.
    virtual void PauseSound(bool) {}

    /// GetAsyncKeyState: a virtual key is held down.
    virtual bool KeyDown(int) { return false; }

    /// Mouse buttons held in the window: 1 left, 2 right, 4 middle.
    virtual int MouseButtons() { return 0; }

    /// The game window is in front (GetForegroundWindow).
    virtual bool Active() { return true; }

    /// Where the mouse is, in pixels of the game's picture.
    virtual std::pair<int, int> MousePosition() { return {0, 0}; }

    /// Moves the mouse to a point of the game's picture (SetCursorPos).
    virtual void SetMousePosition(int, int) {}

    /// The mouse pointer shown over the game, or hidden (SetCursor(NULL)).
    virtual void ShowPointer(bool) {}

    /// Joystick 0 as joyGetPosEx gives it, or none when there is none.
    virtual std::optional<ScnJoystick> Joystick() { return std::nullopt; }
};

/// An opcode the interpreter does not run yet, or a script error.
class ScnException : public std::runtime_error
{
public:
    ScnException(const std::string& message, int slot, int address, int op)
        : std::runtime_error(message), Slot(slot), Address(address), Op(op) {}

    int Slot, Address, Op;
};

/// One slot: its module, and the task running in it.
struct ScnContext
{
    static constexpr int kStackSize = 1000;

    int Slot = 0;
    /// Base of the code being run now (changes with gosub / callmod), for relative addresses.
    int Base = 0;
    /// The slot's own module: code start and size.
    int CodeBase = 0, CodeSize = 0;
    int Entry = 0;
    int Pc = 0;
    /// Address of the instruction being run.
    int Current = 0;
    /// 1 = running.
    int Flags = 0;
    bool Loaded = false;
    /// Slot that registered this one with op_000C, or -1.
    int Parent = -1;
    /// Stack index: 1000 = empty, a push goes below.
    int Sp = kStackSize;
    /// Arguments of callmod frames (result address, arguments, count), and its top.
    int Frames[0x3F0] = {};
    int FrameTop = 0;
    /// Sequential data reader (op_02E4 / 02E5).
    int DataPointer = 0;
    /// Value of the "end" that stopped the task.
    int ExitCode = 0;
    /// op_0083: the surface the task's text goes to (ctx+0x28).
    int TextSurface = 0;
    /// op_0096: the task's text record (ctx+0xFF8).
    int TextRecord = 0;
};

struct ScnOperand
{
    uint8_t Kind = 0;
    bool Relative = false;
    bool AddressOf = false;
    int Value = 0;
    /// The text of an expression (0x11) or a name (0x12 / 0x13), Shift-JIS.
    std::string Text;
};

struct ScnInstruction;

using OpHandler = int (*)(ScnVm& vm, ScnContext& c, const ScnInstruction& i);

struct ScnInstruction
{
    int Op = 0, Address = 0, Length = 0;
    std::vector<ScnOperand> Args;
    std::vector<int> Raw, Targets, CaseValues;
    /// The opcode's handler, looked up once when the instruction is decoded.
    OpHandler Handler = nullptr;
};

} // namespace openshiina::scripting
