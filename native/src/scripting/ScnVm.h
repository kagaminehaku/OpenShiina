// The engine's SCN interpreter as the executable runs it (C# Scripting/ScnVm*.cs; docs/engine-notes.md,
// sections 4 and 10).
//
// 1,000 slots, each both a module and a task: "loadmod" puts a file's code in a slot and starts
// it, "op_000C id, label" makes a slot of a label of the current module (the engine's script
// functions), "gosub" / "callmod" run a slot's code inside the calling task. Every frame the
// scheduler runs each running task until it yields ("op_0034", the per-frame instruction budget,
// or an opcode that returns to the scheduler).
//
// Scripts use real pointers: variables, strings and code live in one flat 32-bit memory
// (ScnAddressSpace). Layout: g / b / a arrays, s flags, f (1,000 per slot), the stacks (1,000 dwords
// per slot, "l" variables and salloc live on it), named variables, a heap for loaded files, and
// the module code.
//
// The C# class is partial, a file for each part of the engine; here the class is declared once,
// by those parts, and each has its own ScnVm.<Part>.cpp.
#pragma once

#include <array>
#include <cstdint>
#include <cstring>
#include <list>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <functional>
#include <initializer_list>

#include "scripting/NetDictionary.h"
#include "scripting/ScnAddressSpace.h"
#include "scripting/ScnOpcodes.h"
#include "formats/MovieAhead.h"
#include "scripting/ScnTypes.h"
#include "scripting/X86Routine.h"

namespace openshiina::scripting {

/// The DirectSound volume of a script volume 0-100 (0x4A15E0, ScnVm.Music; the movies' sound too).
int MusicVolume(int volume);

/// What an embedded routine receives: the variable areas and the stack top (l[0]).
struct ScnNativeArgs
{
    int B = 0, A = 0, S = 0, F = 0, Stack = 0;
};

using NativeRoutine = std::function<void(ScnVm& vm, ScnContext& c, const ScnNativeArgs& a)>;

/// Window events the host passes on to the scripts.
enum class ScnEvent
{
    /// Alt+Enter (slot of op_0794, 0x48808C).
    ToggleFullScreen,
    /// The window lost the focus (op_076D, 0x488094; WM_ACTIVATEAPP with wParam 0).
    Deactivate,
    /// The window got the focus (op_076C, 0x488090; WM_ACTIVATEAPP with wParam 1).
    Activate,
};

/// The window messages the hosts pass on (Windows' numbers).
namespace ScnMessage {
constexpr int Paint = 0x000F, Close = 0x0010, ActivateApp = 0x001C, KeyDown = 0x0100, KeyUp = 0x0101,
              SysKeyDown = 0x0104, Timer = 0x0113, LButtonDown = 0x0201, LButtonUp = 0x0202, LButtonDoubleClick = 0x0203,
              RButtonDown = 0x0204, RButtonUp = 0x0205, MButtonDown = 0x0207, MButtonUp = 0x0208, MouseWheel = 0x020A;

/// lParam of a mouse message: the point in the game's picture.
inline int Point(int x, int y) { return (y << 16) | (x & 0xFFFF); }

/// lParam of WM_KEYDOWN / WM_KEYUP: one press, the key's scan code, and the up / repeat bits.
inline int Key(int scanCode, bool down, bool repeat)
{
    return int(1u | uint32_t(scanCode & 0xFF) << 16 | (repeat || !down ? 1u << 30 : 0u) | (down ? 0u : 1u << 31));
}
} // namespace ScnMessage

/// A rectangle of the screen, (L, T) included to (R, B) excluded; empty when R <= L or B <= T.
struct ScreenArea
{
    int L = 0, T = 0, R = 0, B = 0;

    bool IsEmpty() const { return R <= L || B <= T; }
    int Width() const { return R - L; }
    int Height() const { return B - T; }
    /// The smallest rectangle holding both.
    ScreenArea Union(const ScreenArea& other) const;
    /// The part inside (0, 0)-(width, height).
    ScreenArea Clip(int width, int height) const;
};

class ScnVm
{
public:
    static constexpr int Slots = 1000, StackSize = ScnContext::kStackSize;

    ScnVm(const ScnOpcodes& opcodes, IScnHost& host);
    ~ScnVm();
    ScnVm(const ScnVm&) = delete;
    ScnVm& operator=(const ScnVm&) = delete;

    ScnContext& Slot(int index) { return m_slots[size_t(index)]; }

    /// Adds (or replaces) an opcode outside the core.
    void Register(int op, OpHandler handler) { m_handlers[size_t(op & 0xFFFF)] = handler; }
    /// A handler for the old engines only (OldEngine), in place of the later one.
    void Register234(int op, OpHandler handler) { m_handlers234[op] = handler; }
    void RegisterBefore240(int op, OpHandler handler) { m_handlersBefore240[op] = handler; }
    /// Whether this engine version runs the opcode.
    bool HasHandler(int op) const { return HandlerFor(op) != nullptr; }

    /// The old engines, v2.34 and v2.33 (Ao no Juuai, Ayakashibito, Cleavage).
    bool OldEngine() const { return EngineVersion < 235; }

    /// Instructions a task may run in one frame before it has to yield (DAT_004880C8).
    int Budget = 100000;
    /// op_0033 / op_0032: yield after every instruction.
    bool YieldEveryInstruction = false;
    /// The task asked to quit the game ("end" with a value, or a window close).
    bool QuitRequested = false;

    /// How often each opcode ran.
    std::vector<int64_t> OpCounts = std::vector<int64_t>(0x10000);

    /// Where an instruction ran: slot, code base, offset, opcode.
    using HotSpot = std::tuple<int, int, int, int>;
    /// When set, the instructions run (counted by HotSpot), for comparing runs with the C# player's.
    std::map<HotSpot, int>* HotSpots = nullptr;

    // ---- Memory (ScnVm.cs) ----

    uint8_t ReadByte(int address) { return *At(address); }
    void WriteByte(int address, uint8_t value) { *At(address) = value; }
    uint16_t Read16(int address) { uint16_t v; std::memcpy(&v, At(address), 2); return v; }
    void Write16(int address, int value) { uint16_t v = uint16_t(value); std::memcpy(At(address), &v, 2); }
    int Read32(int address) { int v; std::memcpy(&v, At(address), 4); return v; }
    void Write32(int address, int value) { std::memcpy(At(address), &value, 4); }

    /// The bytes [address, address + length) in place; they must not run past the top of the 4 GB.
    uint8_t* Bytes(int address, int length);
    /// The memory the scripts' parts of the 4 GB have been given, in bytes.
    int64_t ScriptMemoryBytes() const { return m_memory->CommittedBytes(); }
    /// [address, address + length) does not run past the top of the 4 GB.
    bool IsContiguous(int address, int length) const { return ScnAddressSpace::Contiguous(uint32_t(address), length) >= length; }
    /// The bytes [address, address + length) to read: in place, or copied into spare (made larger
    /// when it is short) when they are not one piece of memory or share bytes with [avoid, avoid +
    /// avoidLength), a block the reader writes.
    const uint8_t* ReadView(int address, int length, openshiina::Bytes& spare, int avoid = 0, int avoidLength = 0);
    void WriteBytes(int address, const uint8_t* bytes, int length);
    void WriteBytes(int address, const openshiina::Bytes& bytes) { WriteBytes(address, bytes.data(), int(bytes.size())); }
    void ReadBytes(int address, uint8_t* bytes, int length);
    openshiina::Bytes ReadBytes(int address, int count);
    /// memset.
    void FillMemory(int dst, int count, uint8_t value);
    /// memmove (overlapping blocks of more than 1 MB copied 64 KB at a time from the start).
    void CopyMemory(int dst, int src, int count);
    /// memmove exactly.
    void MoveMemory(int dst, int src, int count);
    /// A zero-terminated string (Shift-JIS), at most 64 KB.
    std::string ReadString(int address);
    int StringLength(int address);
    /// The bytes and a 0.
    void WriteString(int address, const std::string& text);

    /// Zeroed memory (GlobalAlloc / VirtualAlloc); Free gives it back.
    int Allocate(int bytes);
    /// As Allocate, but 0 when the heap has no room.
    int TryAllocate(int bytes);
    /// The block (start, size) that the address is in, or none.
    std::optional<std::pair<int, int>> BlockAt(int address) const;
    /// The lowest block in use at or above the address, or none.
    std::optional<int> NextBlock(int address) const;
    bool IsBlock(int address) const { return m_blocks.count(address) != 0; }
    void Free(int address);
    int AllocateCopy(const uint8_t* data, int length);
    int AllocateCopy(const openshiina::Bytes& data) { return AllocateCopy(data.data(), int(data.size())); }
    /// Bytes of heap in use.
    int64_t HeapInUse = 0;
    int64_t HeapFree();

    /// The game's .exe is large address aware, so its heap may go above 2 GB.
    bool LargeAddressAware();
    void SetLargeAddressAware(bool value) { m_largeAddressAware = value; }
    /// The Characteristics of a Windows executable's file header, or none when it is not one.
    static std::optional<int> PeCharacteristics(const openshiina::Bytes& exe);

    int GAddress(int index) const { return GRegion + 4 * index; }
    int BAddress(int index) const { return BRegion + 4 * index; }
    int AAddress(int index) const { return ARegion + 4 * index; }
    int SAddress(int index) const { return SRegion + index; }
    int FAddress(int slot, int index) const { return FRegion + slot * StackSize * 4 + 4 * index; }
    int StackAddress(int slot, int index) const { return StackRegion + slot * StackSize * 4 + 4 * index; }
    int GetA(int index) { return Read32(AAddress(index)); }
    void SetA(int index, int value) { Write32(AAddress(index), value); }
    int GetB(int index) { return Read32(BAddress(index)); }
    void SetB(int index, int value) { Write32(BAddress(index), value); }

    void Push(ScnContext& c, int value);
    int Pop(ScnContext& c);

    /// Loads a module file into a slot (op_0001 / 0002); start also runs it as a task.
    bool LoadModule(int slot, const std::string& file, bool start);

    /// A drawing opcode showed a picture: the frame is done.
    bool FrameShown = false;
    /// Runs rounds of the engine's main loop until the scripts show a picture or maxRounds have
    /// run. False once the game quits.
    bool RunFrame(int maxRounds = 20000);
    /// The longest sleep (002A, ms) the scripts asked for in this frame.
    int SleepRequested = 0;
    /// Rounds of the main loop run in the current frame so far.
    int FrameRounds = 0;
    /// The task being run now.
    ScnContext* CurrentTask = nullptr;
    /// One round of the main loop: every running task until it yields.
    void RunRound();
    /// A task started or stopped: the scheduler counts the running slots again.
    void TasksChanged() { m_activeCount = -1; }

    ScnException Error(const ScnContext& c, const std::string& message);

    /// The instruction at `at`; one decoded each time it runs (an operand count read from a
    /// variable) is owned by `transient`.
    const ScnInstruction& Decode(ScnContext& c, int at, std::unique_ptr<ScnInstruction>& transient);

    /// Address of a variable operand (FUN_00414CA0).
    int AddressOf(ScnContext& c, const ScnOperand& o);
    /// Value of an operand (FUN_00414E30).
    int Value(ScnContext& c, const ScnOperand& o);
    /// Stores a value into an operand (FUN_004148E0).
    void Store(ScnContext& c, const ScnOperand& o, int value);

    // ---- Named variables, the core opcodes (ScnVm.Core.cs) ----

    /// Address of a named variable, "name" or "name[index]" (FUN_00434F60).
    int NamedAddress(ScnContext& c, const std::string& text);
    /// A global by name, for the host; created when missing.
    int GlobalAddress(const std::string& name) { return NamedAddress(m_slots[0], name); }
    /// Engine switch 0x4B3E08, set by op_D382.
    int SwitchD382 = 0;
    /// Engine switch 0x4B3E0C (op_AA82): without it the engine's file writes do nothing.
    bool FileWritesEnabled = false;
    /// Engine globals 0x13B5198 / 0x13B519C (op_0048 sets them).
    int Global13B5198 = 0, Global13B519C = 0;

    // ---- The calculator (ScnVm.Calc.cs) ----

    /// Microsoft C runtime rand(), as the engine uses it.
    int Rand();
    void SeedRand(int seed) { m_randSeed = uint32_t(seed); }
    /// The value of an expression operand (Shift-JIS text) as an int.
    int Calculate(ScnContext& c, const std::string& text, bool integer = true);
    double CalculateDouble(ScnContext& c, const std::string& text);
    /// The C conversion of a double to int (__ftol), kept to 32 bits.
    static int ToInt(double value);

    // ---- Diagnostics (ScnVm.Diagnostics.cs) ----

    /// Where an address is: "FILE.SCN+offset", or its hex value.
    std::string DescribeAddress(int address) const;

    // ---- System (ScnVm.System.cs) ----

    /// Engine version from RIO.INI ("2.47" gives 247), for op_03C0.
    int EngineVersion = 247;
    /// The window size of RIO.INI (WindowWidth / WindowHeight).
    int ScreenWidth = 800, ScreenHeight = 600;
    /// The save folder as the scripts see it: a path ending with '\' (Shift-JIS).
    std::string DataPath();
    /// The game being played (its scheme's name, "Azu Plus", UTF-8).
    std::string Game;

    // ---- Events (ScnVm.Events.cs) ----

    /// Runs a slot's code from its entry until it ends (FUN_0042BF60); the value of its "end",
    /// or 1 when the slot has no code.
    int RunSlotSync(int slot);
    /// A window event: the message slot first, then the slot registered for it.
    void Notify(ScnEvent e);
    /// A window message the host passes on (keys, mouse buttons, the wheel).
    void WindowMessage(int message, int wParam, int lParam);
    /// The player closes the window (WM_CLOSE): true when it may close.
    bool CloseWindow();
    /// Stop also the games that RIO.INI's Background lets run behind other windows.
    bool PauseInBackground() const { return m_pauseInBackground; }
    void SetPauseInBackground(bool value);
    /// The game stands still (behind another window without Background, or asked to).
    bool Paused() const { return m_paused; }

    // ---- Window (ScnVm.Window.cs) ----

    /// Engine globals set by simple opcodes, by their address in the executable.
    std::unordered_map<int, int> EngineGlobals;
    int EngineGlobal(int address) const;
    int EngineGlobalOr(int address, int otherwise) const;
    /// A stand-in for the game window's handle: never 0.
    static constexpr int WindowHandle = 0x00010001;

    // ---- Paint (ScnVm.Paint.cs) ----

    /// The window's picture: 24-bit BGR rows of ScreenWidth pixels.
    const openshiina::Bytes& Window();
    /// The part of the window's picture that changed since the last call.
    ScreenArea TakeChangedArea();
    /// InvalidateRect: the rectangle is repainted from the display surface by the next WM_PAINT.
    void InvalidateWindow(int l, int t, int r, int b);
    void InvalidateWindow() { InvalidateWindow(0, 0, ScreenWidth, ScreenHeight); }

    // ---- Surfaces (ScnVm.Surfaces.cs) ----

    static constexpr int SurfaceCount = 1024;
    /// Bits per pixel of new surfaces (RIO.INI Bpp; 0x487F44).
    int Bpp = 24;
    int SurfaceAddress(int index) const { return SurfaceRegion + index * SurfaceSize; }
    /// A surface's descriptor field ([2] pixels, [7] width ...).
    int SurfaceField(int index, int field) { return Read32(SurfaceAddress(index) + 4 * field); }
    void CreateSurface(int index, int width, int height, int bpp, int flags);
    void FreeSurface(int index);
    /// The pages the engine makes when it opens its window.
    void MakePages();
    int TableAddress(int index) const;

    // ---- Pictures (ScnVm.Pictures.cs) ----

    /// Room for the picture slots of every version.
    static constexpr int PictureSlots = 0x401;
    /// The size of a file op_00C9 loaded at the address, or -1.
    int LoadedFileSize(int address) const;
    int PictureAddress(int slot) const { return PictureRegion + 4 * slot; }
    /// The picture in a slot (0 when empty).
    int Picture(int slot) { return slot >= 0 && slot < PictureSlots ? Read32(PictureAddress(slot)) : 0; }
    /// FUN_00410520: the address of pixel (x, y) of a picture's frame.
    int PicturePixel(int picture, int frame, int x, int y, int bytesPerPixel);
    /// FUN_004106F0: a blank picture.
    int CreatePicture(int width, int height, int bytesPerPixel, int frames);
    /// FUN_004105F0: fills a rectangle of a raw picture's frame.
    void FillPicture(int picture, int frame, int x, int y, int w, int h, int color);

    // ---- Files (ScnVm.Files.cs) ----

    /// A number from an INI file of the game folder, as GetPrivateProfileInt reads it.
    int IniInt(const std::string& file, const std::string& section, const std::string& key, int fallback);
    /// A value of an INI file of the game folder (as 0105 last wrote it), or none.
    std::optional<std::string> IniString(const std::string& file, const std::string& section, const std::string& key);
    /// A file the scripts open for reading; false when there is none.
    bool ReadScriptFile(const std::string& path, openshiina::Bytes& data) { return ReadScriptFile(path, data, true); }
    /// The length ReadScriptFile gives.
    std::optional<int64_t> ScriptFileSize(const std::string& path);
    /// A file the scripts write: only save files, and only once op_AA82 allowed writes.
    bool WriteScriptFile(const std::string& path, const openshiina::Bytes& data);
    /// The memory the archives' files the scripts read are kept in, decoded.
    int64_t FileCacheBytes = int64_t(64) << 20;

    // ---- Draw (ScnVm.Draw.cs) ----

    /// FUN_004396F0 with MMX: dst = A * a / (a + b) + B * b / (a + b) byte by byte, for width
    /// pixels (3 bytes each) on height rows that all have pitch; source2 below 0x100 is a grey level.
    void BlendRows(int dst, int source, int source2, int a, int b, int width, int height, int pitch);
    /// FUN_00437B40 with MMX: a rule (mask picture) transition, byte by byte over pixels * 3 / 4 dwords.
    void RuleBlend(int dst, int a, int b, int rule, int pixels, int t);

    // ---- Embedded routines (ScnVm.Native.cs) ----

    /// Adds a routine by the signature X86Routine gives for its code.
    void RegisterNative(const std::string& signature, const char* name, NativeRoutine run);
    /// Adds a routine whose builds differ only in code the C++ version does not depend on.
    void RegisterNative(std::initializer_list<const char*> signatures, const char* name, const NativeRoutine& run);
    /// The name of the C++ version registered for a signature, or none.
    std::optional<std::string> NativeName(const std::string& signature) const;
    /// The signature of the routine at an address.
    const std::string& NativeSignature(int address) { return RoutineAt(address).Code.Signature; }
    /// The l[index] value a routine sees: the dword at the stack top + index.
    int NativeArg(const ScnNativeArgs& args, int index) { return Read32(args.Stack + 4 * index); }

    // ---- The zooms and pans of the scripts' x86 code, written out (ScnVm.Subpixel32.cs, ...) ----
    // Positions and sizes in 1/16 pixels; false where the x86 code would not end or for a case the
    // C++ version does not take (see each file).

    /// Oreimo START 76388: a rectangle of a 32-bit picture copied at a fraction of a pixel.
    bool Subpixel32(int dst, int dstPitch, int dtx, int dty, int w, int h, int src, int srcPitch, int stx, int sty, bool v250 = false,
                    bool averageEdge = false, bool bottomTwo = true);
    /// Re: Rem Plus START 866A8: the enlarging case.
    bool Enlarge32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb);
    /// Oreimo START 78380: the scaling-down case.
    bool Scale32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb)
    {
        return Scale32(dst, dstPitch, dl, dt, dr, db, src, srcPitch, sl, st, sr, sb, 0, 0, false);
    }
    /// Scale32 with its tables written to the work buffers (when given) and Can Fes!'s quirk (lastAlways).
    bool Scale32(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb, int columnTable,
                 int rowTable, bool lastAlways);
    /// The number of Scale32 table entries of an axis from its destination start and end.
    static int ScaleEntryCount(int start, int end);
    /// Bitch Nee-chan START C7C70: Scale32's SSE2 build (single floats).
    bool Scale32Sse(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb);
    /// Ero-On! START 5E2D6: the scaling-down case of the first v2.49 build.
    bool Scale16(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb);
    /// Ero-On! START 5DFA5: its enlarging case, with its weight table at table.
    bool Enlarge16(int dst, int dstPitch, int dl, int dt, int dr, int db, int src, int srcPitch, int sl, int st, int sr, int sb, int table,
                   int scale = 257, bool opaque = false);

    // ---- Sound (ScnVm.Sound.cs) ----

    static constexpr int SoundSlots = 0x101;
    /// FUN_00405900: the file as a RIFF WAVE, or none when it is none of the known kinds.
    static std::optional<openshiina::Bytes> SoundToWave(const openshiina::Bytes& data);

    // ---- Sprites (ScnVm.Sprites.cs, ScnVm.SpriteModes.cs) ----

    /// FUN_00439E10: draws count entries at sprites into a 24-bit picture, clipped to clip (left,
    /// top, right, bottom) or the whole picture.
    void Compose(int sprites, int count, int pixels, int width, int height, int pitch, const int* clip);
    /// FUN_004116A0: a frame's rectangle (x, y, x + width, y + height), or none.
    std::optional<std::array<int, 4>> FrameRect(int slot, int frame);
    std::optional<std::array<int, 4>> FrameRectOrLast(int slot, int frame);
    /// FUN_004114B0: the entry under (x, y) gives hit = its word 6.
    bool HitTest(int sprites, int count, int x, int y, int& hit);
    /// StretchBlt SRCCOPY: a copy when the sizes match, else nearest pixels.
    void StretchCopy(int dst, int x, int y, int w, int h, int src, int sx, int sy, int sw, int sh);
    /// The engine's stretch (051E with bit 31).
    void SoftStretch(int dst, int dx, int dy, int dw, int dh, int src, int sx, int sy, int sw, int sh);

    // ---- Layers (ScnVm.Layers.cs) ----

    /// FUN_00411130: draws the sprite entry at entry over a frame of a 32-bit picture.
    void DrawOnPicture(int picture, int frame, int entry);

    // ---- Printf (ScnVm.Printf.cs) ----

    /// wsprintfA into VM memory; returns the length written (without the NUL).
    int Wsprintf(int dest, int format, const std::vector<int>& args);
    /// wvsprintfA: the arguments come one by one from arg.
    int Wvsprintf(int dest, int format, const std::function<int()>& arg);

    // ---- Text (ScnVm.Text.cs) ----

    static constexpr int TextRecords = 38, ScratchRecords = 18;
    int RecordAddress(int index) const { return TextRegion + index * RecordSize; }
    /// BitBlt SRCCOPY between two 24-bit surfaces, clipped to both.
    void BitBlt(int dst, int x, int y, int width, int height, int src, int sx, int sy);
    /// The surface WM_PAINT shows in the window (0x13B43E4).
    int DisplaySurface() const { return EngineGlobal(0x13B43E4); }
    /// The window's picture changed: the host should present it (and clear this).
    bool ScreenInvalidated = false;

    /// The time spent in each embedded routine (by its C++ version's name, "x86" for the others)
    /// while set: the Switch player's log of what makes frames slow.
    struct RoutineTime
    {
        double Milliseconds = 0;
        int Calls = 0;
    };
    std::unordered_map<std::string, RoutineTime>* RoutineTimes = nullptr;
    /// The time spent in each opcode (its handler, with the routines it calls) while set.
    std::unordered_map<int, RoutineTime>* OpTimes = nullptr;

    // ---- Input (ScnVm.Input.cs) ----

    /// Reads joystick 0 whatever RIO.INI says (true), never (false), or as RIO.INI says (none).
    std::optional<bool> Joypad;
    /// FUN_00413630: the buttons held now (0 when the window is not in front).
    int Buttons();
    /// FUN_00413810: the buttons with key repeat for one caller.
    int RepeatButtons(int caller, int buttons);

    // ---- Menus (the part of it the core needs) ----

    /// RIO.INI's Background: the game goes on while another window is in front.
    bool RunsInBackground();

private:
    // Memory regions
    static constexpr int GRegion = 0x01000000, BRegion = 0x02000000, ARegion = 0x03000000, SRegion = 0x04000000;
    static constexpr int FRegion = 0x05000000, StackRegion = 0x06000000, NamedRegion = 0x07000000;
    static constexpr int HeapRegion = 0x10000000, CodeRegion = 0x70000000;
    static constexpr uint32_t HighHeapRegion = 0x90000000, HighHeapEnd = 0xFF000000;

    uint8_t* At(int address) { return m_memory->At(uint32_t(address)); }
    /// The bytes from the address that are one piece of memory, at most length.
    int InPiece(int address, int length) const { return int(ScnAddressSpace::Contiguous(uint32_t(address), length)); }

    OpHandler HandlerFor(int op) const;
    int Execute(ScnContext& c, const ScnInstruction& ins);
    int ActiveCount();
    /// Runs a task until it yields; the code it yielded with (0 frame end, 1 quit).
    int Run(ScnContext& c);
    ScnOperand ReadOperand(int& p, bool declaration = false);
    bool ReadLargeAddressAware();
    void ClearRange(int start, int end) { m_memory->Discard(uint32_t(start), int64_t(end) - start); }
    int LowerFreeRange(int address) const;

    std::unique_ptr<ScnAddressSpace> m_memory;
    int m_heapTop = HeapRegion, m_codeTop = CodeRegion, m_namedTop = NamedRegion;
    int m_highTop = int(HighHeapRegion);
    const ScnOpcodes& m_opcodes;
    IScnHost& m_host;
    std::vector<ScnContext> m_slots = std::vector<ScnContext>(Slots);
    std::unordered_map<int, std::unique_ptr<ScnInstruction>> m_decoded;
    std::vector<OpHandler> m_handlers = std::vector<OpHandler>(0x10000);
    std::unordered_map<int, OpHandler> m_handlers234, m_handlersBefore240;
    // The heap: blocks in use by address with their size, and the free ranges below the top
    std::unordered_map<int, int> m_blocks;
    std::map<int, int> m_freeRanges;
    std::optional<bool> m_largeAddressAware;
    bool m_taskWaited = false;
    int m_activeCount = -1;
    // Decoded instructions taken out while one of them may be running (the C# player's garbage
    // collector keeps them), kept until no instruction runs; how deep Run is
    void Retire(std::unique_ptr<ScnInstruction> instruction) { m_retired.push_back(std::move(instruction)); }
    std::vector<std::unique_ptr<ScnInstruction>> m_retired;
    int m_runDepth = 0;

    // ---- Core ----
    using Scope = NetDictionary<std::string, std::pair<int, int>>;
    void RegisterCore();
    int AllocateNamed(int count);
    void ReleaseScope(Scope& scope);
    void LeaveScopes(int slot, int n);
    std::pair<std::string, int> Declared(ScnContext& c, const std::string& text);
    int If(ScnContext& c, const ScnInstruction& i, int mode);
    /// Two-operand arithmetic: the second operand = f(first, second).
    template <typename F> int Binary(ScnContext& c, const ScnInstruction& i, F f);
    int CallWithArguments(ScnContext& c, const ScnInstruction& i, bool toSlot);
    int AddressOfResult(ScnContext& c, const ScnOperand& o);
    int CompareStrings(int a, int b, bool ignoreCase);
    std::vector<std::vector<std::unique_ptr<Scope>>> m_scopes = std::vector<std::vector<std::unique_ptr<Scope>>>(Slots);
    std::unordered_map<std::string, std::pair<int, int>> m_globals;
    std::unordered_map<int, std::vector<int>> m_namedFree;
    std::vector<std::unique_ptr<Scope>> m_freeScopes;
    int m_caseValue = 0;

    // ---- Calc ----
    class Calculator;
    friend class Calculator;
    std::vector<double>& RegisterArray(char letter);
    std::unordered_map<char, std::vector<double>> m_registers;
    uint32_t m_randSeed = 1;
    int m_seedGiven = 0;

    // ---- Diagnostics ----
    struct ModuleImage
    {
        int Base;
        std::string File;
        openshiina::Bytes Original;
    };
    void RememberModule(int at, const std::string& file, const openshiina::Bytes& code) { m_moduleImages.push_back({at, file, code}); }
    std::vector<ModuleImage> m_moduleImages;
    int64_t m_rounds = 0;
    int m_frameNumber = 0;

    // ---- System ----
    void RegisterSystem();
    /// The registry values the installer wrote: a string (Shift-JIS), a number, or none.
    std::optional<std::string> RegistryString(const std::string& name);
    std::optional<int> RegistryNumber(const std::string& name);
    int InstallMode();
    int FindInstallMode();
    std::optional<std::string> ExeFileName();
    std::string m_registryKey;
    std::optional<int> m_installMode;

    // ---- Events ----
    void RegisterEvents();
    bool MessageHook(int message, int wParam, int lParam);
    void PumpMessages() { Paint(); }
    void FireTimers();
    uint32_t Clock() { return (m_paused ? m_pausedAt : m_host.Milliseconds()) - m_pausedFor; }
    void UpdatePause();
    NetDictionary<int, std::pair<int, uint32_t>> m_timers;
    bool m_paintPending = false;
    bool m_inFront = true, m_paused = false, m_pauseInBackground = false;
    uint32_t m_pausedAt = 0, m_pausedFor = 0;

    // ---- Window ----
    struct ScnTimer
    {
        int Period = 0, Value = 0, Count = 0;
        uint32_t Start = 0;
    };
    struct ClockRead
    {
        int Frame, Paints, Pc;
    };
    static constexpr int Kernel32Module = 0x7C800000;
    static constexpr int MaxControls = 0x100, ControlHandle = 0x00090001;
    void RegisterWindow();
    int SetGlobal(int address, ScnContext& c, const ScnInstruction& i);
    int WindowMessage(ScnContext& c, const ScnInstruction& i);
    std::unordered_map<int, ScnTimer> m_engineTimers;
    std::unordered_map<int, uint32_t> m_inputWaits;
    std::unordered_set<int> m_virtualBlocks;
    int m_paints = 0;
    std::unordered_map<int, int> m_taskPaints;
    std::unordered_map<int, ClockRead> m_clockReads;
    std::unordered_map<int, std::pair<int, int>> m_timeReads;
    std::unordered_map<int, int> m_classLongs;
    std::unordered_map<int, std::string> m_controls;
    std::string m_windowTitle;

    // ---- Paint ----
    void Paint();
    void BltToWindow(int l, int t, int r, int b, int src, int sl, int st, int sr, int sb);
    void PresentedByDirect3D(int l, int t, int r, int b);
    void RunPaintSlot(int global);
    openshiina::Bytes m_window;
    std::vector<std::array<int, 4>> m_invalid;
    ScreenArea m_changed{0, 0, 0x7FFFFFFF, 0x7FFFFFFF};

    // ---- Movie ----
    struct Movie
    {
        /// +0: 0 none, 1 stopped, 2 paused, 3 running.
        int State = 0;
        std::string Name;
        /// +0x10: the movie starts again at its end.
        int Loop = 0;
        /// +0x18: how many times it started again (-1 after a stop).
        int LoopCount = 0;
        int Surface = 0;
        int Width = 0, Height = 0;
        /// +0x34: volume 0-100.
        int Volume = 100;
        std::unique_ptr<formats::MovieAhead> Video;
        /// Frames decoded since the start, and the time frame 0 was due.
        int Decoded = 0;
        uint32_t Start = 0, PausedAt = 0;
        /// The movie's sound (its MPEG audio stream; empty when it has none) and its music stream while it plays.
        openshiina::Bytes Wave;
        int Sound = 0;
    };
    static constexpr int MovieSlots = 16;
    void RegisterMovie();
    Movie* MovieSlot(int n) { return n >= 0 && n < MovieSlots ? &m_movies[size_t(n)] : nullptr; }
    bool OpenMovie(Movie& movie);
    bool OpenMovieFile(Movie& movie, const std::string& name);
    bool RunMovie(Movie& movie);
    void UpdateMovie(Movie& movie);
    void DrawMovieFrame(const Movie& movie, const openshiina::Bytes& frame);
    void MovieSoundStart(Movie& movie, int64_t milliseconds = 0);
    void MovieSoundStop(Movie& movie);
    void MovieSoundPause(Movie& movie);
    void MovieSoundResume(Movie& movie);
    void MovieSoundVolume(Movie& movie, int volume);
    void SurfaceToPicture(int picture, int frame, int x, int y, int w, int h, int surface, int sx, int sy);
    std::array<Movie, MovieSlots> m_movies{};

    // ---- Surfaces ----
    static constexpr int SurfaceRegion = 0x08000000, SurfaceSize = 0x2C;
    void RegisterSurfaces();
    bool m_pagesMade = false;
    std::unordered_map<int, int> m_tables;

    // ---- Pictures ----
    static constexpr int PictureRegion = 0x09300000;
    int MaxPictureSlot() const { return EngineVersion >= 249 ? 0x400 : 0x100; }
    void RegisterPictures();
    void ReleasePicture(int slot);
    void FreeScriptBlock(int block);
    void RelocatePicture(int picture);
    void PredecodePicture(int picture);
    int FrameBytes(int picture, int frame);
    std::vector<bool> m_pictureOwned = std::vector<bool>(PictureSlots);
    std::unordered_map<int, int> m_fileSizes;

    // ---- Files ----
    struct ResolvedPath
    {
        bool Save;
        std::string Name;
    };
    struct OpenFile
    {
        openshiina::Bytes Data;
        int Position = 0;
    };
    void RegisterFiles();
    void FolderMade(const std::string& path);
    bool InMadeFolder(const std::string& path, bool folderItself);
    std::optional<ResolvedPath> ResolvePath(const std::string& path);
    const openshiina::Bytes* ModuleFile(const std::string& name);
    bool ReadScriptFile(const std::string& path, openshiina::Bytes& data, bool load);
    bool ReadArchiveFile(const std::string& name, openshiina::Bytes& data);
    std::optional<std::string> TranslationByFileName(const std::string& name);
    std::optional<int64_t> TranslationFileSize(const std::string& name);
    void ForgetKept();
    void MountArchive(const std::string& archive, bool mounted);
    void Kept(const std::string& name, const openshiina::Bytes& data);
    bool IsSaveFolder(const std::string& path);
    bool ReadSave(const std::string& name, openshiina::Bytes& data) { return m_host.ReadSaveFile(name, data); }
    void WriteSave(const std::string& name, const openshiina::Bytes& data) { m_host.WriteSaveFile(name, data); }
    void DeleteSave(const std::string& name) { m_host.DeleteSaveFile(name); }
    std::string m_iniFile;
    std::map<std::tuple<std::string, std::string, std::string>, std::string> m_iniWritten;
    std::unordered_map<int, OpenFile> m_files;
    int m_nextHandle = 0x100;
    std::vector<std::string> m_madeFolders;
    std::optional<std::string> m_moduleFile;
    openshiina::Bytes m_moduleCopy;
    bool m_moduleCopyLooked = false, m_moduleCopyFound = false;
    // The files kept, the last read first: name (folded) -> its place in the order
    std::list<std::pair<std::string, openshiina::Bytes>> m_fileCacheOrder;
    std::unordered_map<std::string, std::list<std::pair<std::string, openshiina::Bytes>>::iterator> m_fileCache;
    int64_t m_fileCacheSize = 0;

    // ---- Draw ----
    void RegisterDraw();
    void ShowAtLevel(int source, int level);
    // op_0500: the brightness the display surface was made with last (0x482D38)
    int m_screenLevel = -1;

    void RegisterPrintf();

    // ---- Sprites ----
    static constexpr int SpriteLists = 256;
    struct SpriteBlend;
    struct RunHeader
    {
        int Method, Count, Data;
    };
    /// FUN_004396B4: table[v] ~ v * weight / total, stepping a counter that starts at total / 2.
    static void StepTable(uint8_t* table, int weight, int total);
    void RegisterSprites();
    int SpriteEntry(int list, int index) const;
    static bool SpriteDrawable(int& flags);
    void DrawSprite(int entry, int flags, const void* target);
    RunHeader ReadRun(int p);
    const uint8_t* ReadRow(int p, int dst, int bytes, int pixels);
    void DrawRowPlain(int p, int dst, int skip, int visible, const SpriteBlend* blend);
    static void DrawRun(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend* blend);
    static void DrawRunAlpha(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend& blend);
    static void DrawRunTint(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend& blend);
    static void DrawRunAdd(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, int address, const SpriteBlend& blend);
    static void DrawRunOpaque(int method, int n, const uint8_t* src, int s, uint8_t* row, int d);
    static void DrawRunMask(int method, int n, const uint8_t* src, int s, uint8_t* row, int d);
    void DrawRowSilhouette(int p, int dst, int skip, int visible, const SpriteBlend& blend);
    static void PaintTint(int method, int n, const uint8_t* src, int s, uint8_t* row, int d, const SpriteBlend& blend);
    static void PaintPending(int n, uint8_t* row, int d, int address, const SpriteBlend& blend);
    void CopyPixel24(int dst, int src);
    std::vector<int> m_spriteCounts = std::vector<int>(SpriteLists);
    int m_spriteList = 0;
    // The list 04B8 was given, for 04B7
    int m_spriteListSet = 0;
    std::array<int, 4> m_lastFrameRect = {};

    // ---- Sound ----
    IScnSound* Sound() { return m_host.Sound(); }
    void RegisterSound();
    int OggLength(int address);
    std::optional<openshiina::Bytes> ToWave(const openshiina::Bytes& data);
    int SoundBuffer(int value) const;
    int MakeSoundOrSilence(int slot, const std::optional<openshiina::Bytes>& wave);
    bool MakeSound(int slot, const openshiina::Bytes& wave);
    std::vector<int> m_sounds = std::vector<int>(SoundSlots);
    int m_nextSound = 0x10000;
    // The volume 06A9 last set each buffer to (06AC)
    std::unordered_map<int, int> m_soundVolumes;

    // ---- Music ----
    struct MusicStream
    {
        int Handle = 0;
        int Status = 0;
        int Volume = 100;
        int LoopCount = 0;
        bool Looping = false;
        bool Compressed = false;
        int StartMs = 0, LoopMs = -1;
        int SampleRate = 0;
        // The whole stream in ms (06F0)
        int LengthMs = 0;
        // 06EC's fade: a step of Delta every StepMs up to Target (the stream stops there when
        // StopAtEnd and it went down), the time the last step was due; StepMs 0: none
        int FadeStepMs = 0, FadeDelta = 0, FadeTarget = 0;
        bool FadeStopAtEnd = false;
        /// v2.33's fade (06DD / 06DE): no target, it ends past 100 or 0.
        bool FadeOld = false;
        uint32_t FadeDue = 0;
    };
    static constexpr int MusicSlots = 256;
    IScnMusic* Music() { return m_host.Music(); }
    void RegisterMusic();
    MusicStream* Stream(int handle) { return m_music.Find(handle); }
    void FadeTo(MusicStream* s, int ms, int delta, int target);
    int MusicLength(int address);
    std::vector<int> m_musicTable = std::vector<int>(MusicSlots);
    NetDictionary<int, MusicStream> m_music;
    int m_nextMusic = 0x20000;

    // ---- Menus ----
    void RegisterMenus();
    int Compare16(int x, int y);
    bool IsCompare16Slot(int slot);
    void SortElements(int lo, int count, int width, int slot);
    // Handles of the menus 0898 made
    int m_nextMenu = 0x00510000;

    // ---- Effects ----
    static bool ClipRect(int* r, int* follow, int width, int height);
    void Shown(int surface);
    void ShownOnWindow(int surface, int l, int t, int r, int b);
    /// FillRect of a surface with a colour (0x00BBGGRR); 32-bit pixels get 0 in their fourth byte.
    void FillSurface(int surface, int l, int t, int r, int b, int color);
    void RegisterEffects();
    void RippleTables(int surface);
    int AddSubtract(ScnContext& c, const ScnInstruction& i, bool add);
    void AddSubtract(int dst, int x, int y, int w, int h, int src, int sx, int sy, int a, bool add);
    void Mosaic(int dst, int x, int y, int w, int h, int src, int sx, int sy, int size);
    void Wave(const int* v);
    void Ripple(int dst, int src, int phase, int frequency, int height);
    void PictureToPicture(int dstPicture, int dstFrame, int x, int y, int srcPicture, int srcFrame, int sx, int sy, int w, int h);
    void ConvertInPlace(int to, int from, int line, int srcBytes);
    void CopyForward(int dst, int src, int count);
    void RotateZoom(const int* v);
    // FUN_00419AC0's distance and angle from the centre of each pixel of a quarter of the surface
    // (0x4C7D10 / 0x4C7D14), made again when the size changes
    std::vector<int> m_rippleDistance;
    std::vector<uint8_t> m_rippleAngle;
    int m_rippleW = -1, m_rippleH = -1;

    // ---- Gdi ----
    // GDI objects made by the scripts: brushes (colour) and pens, by handle
    struct GdiObject
    {
        bool Pen;
        int Color, Style, Width;
    };
    void RegisterGdi();
    std::optional<int> DllCall(const std::string& dll, const std::string& function, const std::vector<int>& a);
    std::pair<openshiina::Bytes, int>* Resource(int handle);
    std::optional<openshiina::Bytes> ExeResource(int name, int type);
    void Write64(int address, int64_t value);
    int NewGdiObject(const GdiObject& o);
    bool GdiEllipse(int dc, int l, int t, int r, int b);
    std::unordered_map<int, GdiObject> m_gdiObjects;
    // What each device context has selected (a new one: the white brush and the black pen)
    std::unordered_map<int, std::pair<int, int>> m_gdiSelected;
    int m_nextGdiObject = 0x00060001;
    // The font files added (AddFontMemResourceEx), for their handles
    int m_memoryFonts = 0;
    // SetWindowLongA's values, by window and index
    std::map<std::pair<int, int>, int> m_windowLongs;
    // The executable's resources found (FindResourceA), their handles ResourceHandles + 1, + 2...,
    // and where LockResource copied each
    std::vector<std::pair<openshiina::Bytes, int>> m_resources;

    // ---- RioSub ----
    void RioChangeHsl(int pixels, int width, int height, int stride, int saturation, int light);
    void RioChangeColorBalance(int pixels, int width, int height, int stride, int red, int green, int blue, int mode);
    void RioChangeCbHsl(int pixels, int width, int height, int stride, int red, int green, int blue, int mode, int saturation, int light);

    // ---- Engine250 ----
    /// A character drawn from a picture of its own (009C): its code, size and bytes.
    struct Glyph250
    {
        uint16_t Code;
        int Width, Height;
        openshiina::Bytes Pixels;
    };
    void RegisterEngine250();
    void BoxBlur250(int dstSlot, int dstFrame, int x, int y, int w, int h, int srcSlot, int srcFrame, int sx, int sy, int rx, int ry);
    /// 077A w, h (0xD09E30 / 0xD09E34): a size the v2.50 START sets at start (1280 x 720).
    std::pair<int, int> Size250;
    // 009C's table: per text record (the task's, ctx+0x1004), by index (0x539D48 + record * 0x9000
    // + index * 36; the engine keeps its font glyphs in the same table, after index 0x1FF)
    std::map<std::pair<int, int>, Glyph250> m_glyphs250;
    // The glyph Glyph250For last gave
    ScnGlyph m_glyph250;

    // ---- Text ----
    static constexpr int TextRegion = 0x09000000, RecordSize = 0x161C;
    void RegisterText();
    int G(int rec, int field);
    void S(int rec, int field, int value);
    void ResetRecord(int index);
    void SetText(int rec, int x, int y, int text);
    void ClearGlyphs(int rec);
    bool DeleteFont(int font);
    void ReleaseFont(int rec);
    void CreateRecordFont(int rec);
    void RenewFont(int rec);
    int SelectedFont(int rec);
    std::pair<int, int> Extent(int font, const uint8_t* text, size_t length);
    openshiina::Bytes CharBytes(int rec, int length);
    int ParseNumber(int rec);
    int NamedValue(int rec);
    void ReadField(int rec, int dst, int max);
    bool PrevIsComma(int rec);
    bool FindIn(int haystack, const openshiina::Bytes& needle);
    void SwapRecords(int other, int rec);
    void ControlCode(int rec, int surface);
    void FontChanged(int rec);
    void UseColor(int rec, int offset);
    void AlignLine(int rec, int surface);
    void MeasureLine(int rec);
    void PushMacro(int rec);
    void DefineMacro(int rec);
    void Tilde(int rec, int at);
    int TakeChar(int rec);
    void RubyStart(int rec);
    void TextStep(int rec, int surface, bool invalidate);
    std::pair<int, int> FixedOr(int rec, int font, const openshiina::Bytes& ch, int length);
    bool DrawChar(int rec, int surface, bool invalidate);
    void FindIndent(int rec, int font, bool dc);
    void DrawWithEffects(int rec, int surface, bool invalidate, int font, const openshiina::Bytes& ch, int length);
    void DrawGlyph(int rec, int surface, int font, int x, int y, const openshiina::Bytes& ch, int length);
    void DrawGlyphOnLayer(int rec, const ScnGlyph& glyph, int src, int left, int top, uint32_t firstRow, uint32_t endRow, uint32_t firstCol,
                          uint32_t endCol, int alpha);
    int CurrentRecord(const ScnContext& c) const { return RecordAddress(c.TextRecord); }
    /// v2.50: a character of 009C's pictures, before the font's, or null.
    const ScnGlyph* Glyph250For(int record, int code);
    // The glyphs each record has made (0x4FC550), cleared with its font
    std::array<std::unordered_map<int, ScnGlyph>, TextRecords> m_glyphs;
    // Fonts that exist: a deleted handle stays in records and fails as GDI's would
    std::unordered_set<int> m_liveFonts;
    // What each live font was made from, and the glyphs and ascents made with such fonts
    std::unordered_map<int, ScnFontRequest> m_fontRequests;
    std::map<std::pair<ScnFontRequest, int>, ScnGlyph> m_madeGlyphs;
    std::map<ScnFontRequest, int> m_madeAscents;
    // "_(" / "_)" saved positions (0x5E3A68 / 0x5E3AB4)
    int m_savedX[19] = {}, m_savedY[19] = {};
    // The record "{" ruby text is laid out with (0x485500, "_u")
    int m_rubyRecord = 16;
    // The local buffer of "_i/text/"
    int m_indentScratch = 0;
    // 008C's copy of a record (one for all of them, as the engine's)
    openshiina::Bytes m_savedRecord = openshiina::Bytes(RecordSize);
    // Each record's character set and pitch and family for CreateFontA ("_C"), by its address
    std::unordered_map<int, std::pair<int, int>> m_fontCharSets;

    // ---- Input ----
    struct RepeatState
    {
        int Phase = 0, Last = 0;
        uint32_t Start = 0;
        int Delay = 0;
    };
    void RegisterInput();
    bool KeyHeld(int virtualKey);
    bool JoypadOn();
    int WaitHere(ScnContext& c, int phase);
    std::unordered_map<int, RepeatState> m_repeat;
    int m_repeatDelay = -1, m_repeatSpeed = -1, m_joypad = -1;
    std::unordered_map<int, int> m_buttonWait;

    // ---- Native ----
    struct NativeEntry
    {
        std::string Name;
        NativeRoutine Run;
    };
    struct RoutineInfo
    {
        explicit RoutineInfo(ScnVm& vm, int address) : Code(vm, uint32_t(address)) {}
        X86Routine Code;
        std::optional<NativeEntry> Native;
    };
    void RegisterNativeCall();
    // ---- Engine234, Engine233: the old engines' opcodes ----
    void RegisterEngine234();
    void RegisterEngine233();
    static int PolledOld(ScnVm& vm, ScnContext& c, const ScnInstruction& i);
    int SleepOld(ScnContext& c, int ms);
    void Crossfade(int from, int to, int t);
    int FadeOld(ScnContext& c, const ScnInstruction& i, int sign);
    struct Fade234
    {
        uint32_t Start;
        int From, To;
    };
    // 0502's fades by task; 0028's waits and 050A's crossfades by task (when they started); the frame
    // each task last read the mouse or the time in (by task and opcode), and the later engines'
    // handlers of those opcodes; 03BB's stopwatch
    std::unordered_map<int, Fade234> m_fades234;
    std::unordered_map<int, uint32_t> m_sleeps234, m_crossfades;
    std::map<std::pair<int, int>, int> m_polled234;
    std::unordered_map<int, OpHandler> m_polledLater;
    uint32_t m_stopwatch = 0;

    // ---- Misc ----
    void RegisterMisc();
    /// A file 00CE loaded, into script memory.
    void Loaded(const openshiina::Bytes& data);
    int m_loaded = 0;
    /// The disc 0A30 found: the root of its drive ("Q:\") and whether it is the player's own.
    std::optional<std::pair<std::string, bool>> m_disc;

    // ---- Layers ----
    void RegisterLayers();
    void DrawSprite32(int entry, int pixels, int targetWidth, int targetHeight, int pitch);
    void DrawRow32(int p, int dst, int skip, int visible);
    void DrawRun32(int method, int n, int data, int dst);
    int PictureFrame(int slot, int frame);
    void BlendPictures(const int* v);
    int LayerEntry();
    // A run's source bytes when they cannot be read in place (they share bytes with the target)
    openshiina::Bytes m_runSource;
    int m_layerEntry = 0;

    void RegisterNativeKernels();
    void RegisterNativeRoutines();
    // ScnVm.NativeKernels.cpp's helpers and the routines it calls
    void Enlarge16Native(ScnContext& c, const ScnNativeArgs& a, int scale, bool opaque);
    void RuleAlpha(ScnContext& c, const ScnNativeArgs& a, bool invert);
    void EnlargeMovie(ScnContext& c, const ScnNativeArgs& a);
    void ScaleArea(ScnContext& c, const ScnNativeArgs& a, bool opaque);
    bool Blur32(int dst, int dstStride, int w, int h, int src, int srcStride, int rx, int ry);
    void BlurColumns(int dst, int stride, int first, int end, int h, int r);
    RoutineInfo& RoutineAt(int address);
    std::optional<NativeEntry> NativeByPattern(int address);
    /// Runs the routine being called as x86 code (the cases a C++ version leaves to it).
    void RunX86(ScnContext& c, const ScnNativeArgs& args);
    void SetL(const ScnNativeArgs& args, int index, int value) { Write32(args.Stack + 4 * index, value); }
    // ScnVm.NativeRoutines.cpp's helpers
    void TextWidth(ScnContext& c, const ScnNativeArgs& a, int wide);
    void Mask(ScnContext& c, const ScnNativeArgs& a, int stride);
    template <typename F> void MapDwords(int dst, int src, int bytes, F f);
    void EnlargeWhole(ScnContext& c, const ScnNativeArgs& a, int n, int width, int height, int srcBack = 0, int dstBack = 0);
    void ClearFrame(ScnContext& c, const ScnNativeArgs& a);
    void EnlargeThreeHalves(ScnContext& c, const ScnNativeArgs& a, int pairs, int pairRows, int srcBack = 0, int dstBack = 0);
    void CopyRows(ScnContext& c, const ScnNativeArgs& a, int dst, int dstStride, int src, int srcStride, int rows, int rowBytes);
    void XorStream(ScnContext& c, const ScnNativeArgs& a, int count);
    void Thumbnail(ScnContext& c, const ScnNativeArgs& a, int block, int columns, int rows, int stride = 2400);
    void ThumbnailAt(ScnContext& c, const ScnNativeArgs& a, int block, int columns, int rows, int dst, int src, int stride = 2400,
                     int dstStride = 0);
    void BlockCopy(ScnContext& c, const ScnNativeArgs& a, int rows, int rowBytes, int stride);
    void BlankS25(ScnContext& c, const ScnNativeArgs& a, bool clear);
    void Samples(ScnContext& c, const ScnNativeArgs& a, int n, int stride = 3840);
    void To32Bits(ScnContext& c, const ScnNativeArgs& a, int width, int height);
    void Move16(ScnContext& c, const ScnNativeArgs& a);
    void Saturation(ScnContext& c, const ScnNativeArgs& a, int tableOffset);
    std::array<uint8_t, 3> SaturationPixel(int tables, int k, int b, int g, int r);
    void MontageFind(ScnContext& c, const ScnNativeArgs& a);
    void AddDwords(ScnContext& c, const ScnNativeArgs& a, int value);
    void MatchRecords(ScnContext& c, const ScnNativeArgs& a);
    std::unordered_map<std::string, NativeEntry> m_natives;
    std::unordered_map<int, std::unique_ptr<RoutineInfo>> m_routines;
    // The routine being called
    int m_nativeTarget = 0;

    // ---- Input, Menus, Misc, Text, Warp, GraphMovie, Music (parts the core needs) ----
    void MouseWheel(int delta) { EngineGlobals[0x13B52B4] = delta >= 0 ? 1 : -1; }
    std::string RioSection();
    void SetRunsInBackground(bool value);
    std::optional<bool> m_background;
    std::optional<int> VirtualDiscSize(ScnContext& c, const ScnInstruction& i, const std::string& path);
    void StepTaskText(ScnContext& c);
    // A task's 0083 text went on in this round without waiting for time
    bool m_textWentOn = false;
    void ForgetRoutines();
    void PlayFromModule(ScnContext& c, const std::string& file);
    void PumpGraphMovies();
    void StepMusicFades();
};

/// Shift-JIS text compared as the C# player's OrdinalIgnoreCase compares it decoded.
bool SjisEqualsIgnoreCase(const std::string& a, const std::string& b);
bool SjisEndsWithIgnoreCase(const std::string& text, const std::string& ending);
bool SjisStartsWithIgnoreCase(const std::string& text, const std::string& start);
/// A key of Shift-JIS text without regard to case (UTF-8, folded).
std::string SjisFold(const std::string& text);
/// The text without white space at its ends (as .NET's Trim: ASCII white space and the
/// ideographic space 81 40).
std::string SjisTrim(const std::string& text);
/// Where a character (ASCII) is first / last, from `from`, a byte of a double-byte character
/// never taken for it (as in the decoded text the C# player searches); npos when it is not there.
size_t SjisFind(const std::string& text, char c, size_t from = 0);
size_t SjisFindLast(const std::string& text, char c);
/// .NET's int.TryParse: white space, a sign, the digits, white space; false when that is not all
/// or it does not fit.
bool NetTryParseInt(std::string_view text, int& value);

} // namespace openshiina::scripting
