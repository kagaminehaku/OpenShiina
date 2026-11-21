// Boots a game's SCN scripts in ScnVm without a screen, the way the engine starts (RIO.INI
// "Scn=start.scn" into slot 0), and runs frames until the scripts reach an opcode the
// interpreter does not run yet. Prints that opcode, where it is, and the opcodes run so far.
//
//   scnboot [game folder] [frames] [picture folder] [every n frames]
// Time runs at 60 frames a second whatever the speed of the run. SCNBOOT_PRESS="frame:vk[:frames],..."
// holds virtual keys down (default 3 frames), e.g. "700:0x0D" presses Return at frame 700.
// SCNBOOT_MOUSE="frame:x,y[:buttons[:frames]];..." puts the mouse at (x, y) from that frame on and
// holds buttons (1 left, 2 right, 4 middle; default none) for some frames (default 3).
// With a picture folder, the window's picture is saved as frame_NNNN.png every n frames
// (default 10) and when the run ends. SCNBOOT_INI="Key=value;..." changes keys of RIO.INI as the
// scripts read it (the file stays as it is). SCNBOOT_JIT=0 runs every embedded x86 routine without
// a C# version on the interpreter instead of translating it (X86Jit); SCNBOOT_JIT=sync translates
// a routine before its first call instead of in the background. SCNBOOT_JOY="frame:x,y[:buttons[:frames]];..."
// moves joystick 0 (0-65535, 32767 in the middle; buttons 1 and 2) for some frames (default 3)
// and has the scripts read it whatever RIO.INI's Joypad says. SCNBOOT_WHEEL="frame[:-],..." turns the
// mouse wheel one notch away from the user (":-" towards) at those frames. Presses and clicks are
// also sent as the window messages (WM_KEYDOWN / UP, WM_xBUTTONDOWN / UP) when they start and end.
// SCNBOOT_CLOSE=frame closes the window then (WM_CLOSE: the scripts' answer is printed).
// SCNBOOT_PLAYFROM="MAK00.TXT:64" starts the game at that line of a scenario, as OpenShiina
// Tools' "Play from here" (ScnVm.Warp.cs), and prints the scenario lines shown ("[line]");
// SCNBOOT_SWEEP=1 runs that scenario through and says how it ended (below; tests/sweep.ps1 runs
// every scenario of a game so). SCNBOOT_LIST=1 only lists the scenarios with message lines.
// SCNBOOT_DUMP="frame:address:length:file,..." writes the scripts' memory (hex address) to a file
// after that frame. SCNBOOT_POKE="b301=7,g5=1" keeps those variables (g, a or b and the index) at
// those values: written before every frame, so a game whose story starts at a variable's scene
// (EDEN IV's b[301], as its scene list sets it) is started there.
// SCNBOOT_AREAS=1 checks ScnVm.TakeChangedArea: a copy of the window's picture updated only in the
// areas it gives must be the picture after every frame (the players copy only those).
// SCNBOOT_FOCUS="frame:0|1,..." has the window lose (0) or get (1) the focus at those frames; a game
// that stands still behind (ScnVm.Paused) gets it back in the same frame, the time until then
// passing on the clock alone. SCNBOOT_PAUSE=1 pauses every game behind (PlayerSettings.PauseInBackground).
// The game folder defaults to games\GrandCross\俺妹プラス (or games\GrandCross\Done\俺妹プラス, where
// the games that play through are kept) in a folder above this program.

using System.Text;
using OpenShiina.Archives;
using OpenShiina.Scripting;
using OpenShiina.Game;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string? folder = args.Length > 0 && args[0].Length > 0 ? args[0] : null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); folder == null && d != null; d = d.Parent)
    folder = new[] { Path.Combine(d.FullName, "games", "GrandCross", "俺妹プラス"), Path.Combine(d.FullName, "games", "GrandCross", "Done", "俺妹プラス") }
        .FirstOrDefault(Directory.Exists);
int frames = args.Length > 1 ? int.Parse(args[1]) : 600;
string? pictures = args.Length > 2 && args[2].Length > 0 ? args[2] : null;
int every = args.Length > 3 ? int.Parse(args[3]) : 10;
if (pictures != null)
    Directory.CreateDirectory(pictures);
if (folder == null)
{
    Console.WriteLine("No game folder.");
    return 1;
}

// RIO.INI: engine version and the first script
var ini = GameData.EngineIni(folder) is { } iniPath ? File.ReadAllText(iniPath, Encoding.GetEncoding(932)) : "";
string version = System.Text.RegularExpressions.Regex.Match(ini, @"v(\d+\.\d+)").Groups[1].Value;
string start = System.Text.RegularExpressions.Regex.Match(ini, @"(?im)^Scn=(.+)$").Groups[1].Value.Trim();
var scheme = GameData.FindArchive(folder) is { } archive ? FormatManager.Instance.SchemeForArchive(archive) : null;
if (scheme == null)
{
    Console.WriteLine("The game was not recognised.");
    return 1;
}
using var data = GameData.Open(folder, scheme);
// SCNBOOT_LIST=1: the scenarios with message lines, a line each ("name<tab>lines"), for tests/sweep.ps1
if (Environment.GetEnvironmentVariable("SCNBOOT_LIST") == "1")
{
    var sjis = Encoding.GetEncoding(932);
    foreach (var (name, scenario) in ScenarioText.ReadScenarios(folder, scheme))
        if (sjis.GetString(scenario).Split('\n').Count(l => l.Trim() is { Length: > 0 } t && t[0] is not (';' or '$' or '【')) is > 0 and int lines)
            Console.WriteLine($"{name}\t{lines}");
    return 0;
}
Console.WriteLine($"{scheme.Name}, engine v{version}, first script {start}");

// Save data of the test run: a temporary folder, never the player's real data (SCNBOOT_SAVES=folder:
// one of its own, for runs side by side that must not read or hold each other's saves)
string saves = Environment.GetEnvironmentVariable("SCNBOOT_SAVES") is { Length: > 0 } ownSaves ? ownSaves
    : Path.Combine(Path.GetTempPath(), "openshiina-scnboot", PlayerFolders.FolderName(scheme.Name));
Directory.CreateDirectory(saves);
var host = new Host(data, saves);
var vm = new ScnVm(ScnOpcodes.ForVersion(version, scheme.Name), host)
{
    Game = scheme.Name,
    EngineVersion = (int)Math.Round(double.Parse(version, System.Globalization.CultureInfo.InvariantCulture) * 100),
};
foreach (var (key, set) in new (string, Action<int>)[] { ("WindowWidth", v => vm.ScreenWidth = v), ("WindowHeight", v => vm.ScreenHeight = v) })
    if (System.Text.RegularExpressions.Regex.Match(ini, $@"(?im)^{key}=(\d+)") is { Success: true } m)
        set(int.Parse(m.Groups[1].Value));
// SCNBOOT_VERIFY_NATIVE=1 checks every C# routine against its x86 code; =sig,sig... only those
// whose signatures start so (the large zooms take long on the interpreter)
string? verifyNative = Environment.GetEnvironmentVariable("SCNBOOT_VERIFY_NATIVE");
vm.VerifyNatives = verifyNative is { Length: > 0 } && verifyNative != "0";
if (vm.VerifyNatives && verifyNative != "1")
    vm.VerifyOnly = verifyNative!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
// SCNBOOT_BGLOAD=1: the background loader (00CE) reads on another thread, as in the players
// (and the files a scenario names are read ahead: ScnVm.ReadAhead.cs; SCNBOOT_AHEAD=0 turns that off)
// (the frames then depend on the machine's timing: off by default)
vm.LoadInBackground = Environment.GetEnvironmentVariable("SCNBOOT_BGLOAD") == "1";
vm.ReadAhead = Environment.GetEnvironmentVariable("SCNBOOT_AHEAD") != "0";
// SCNBOOT_CACHE=n: the file cache n MB (default as the players' Auto: by the machine's memory)
vm.FileCacheBytes = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_CACHE"), out int cacheMb) && cacheMb > 0
    ? (long)cacheMb << 20 : FileCachePresets.Auto();
// SCNBOOT_GPU=1: the GPU mode (Vulkan)
if (Environment.GetEnvironmentVariable("SCNBOOT_GPU") == "1")
{
    vm.Accelerator = OpenShiina.Gpu.VulkanAccelerator.TryCreate(out string? gpuError);
    Console.WriteLine(vm.Accelerator != null ? $"GPU: {vm.Accelerator.Name}" : $"No GPU: {gpuError}");
}
if (Environment.GetEnvironmentVariable("SCNBOOT_JOY") is { Length: > 0 })
    vm.Joypad = true;
if (Environment.GetEnvironmentVariable("SCNBOOT_JIT") is { } jit)
{
    vm.JitX86 &= jit != "0";
    vm.JitInBackground = jit != "sync";
}
// SCNBOOT_PROFILE_FRAME=n: the opcodes and routines that took the time of frame n alone
int profileFrame = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_PROFILE_FRAME"), out var pf) ? pf : -1;
if (Environment.GetEnvironmentVariable("SCNBOOT_PROFILE") == "1" || profileFrame >= 0)
    vm.OpTimes = new();
// SCNBOOT_PLAYFROM="FILE.TXT:line": OpenShiina Tools' "Play from here" (ScnVm.Warp.cs), the game
// started at that line of a scenario; the scenario lines it shows are printed as they come
// SCNBOOT_SWEEP=1 with it runs that scenario through: Ctrl held (the games' skip; let go 24
// frames of 48, for choices), Return pressed every 12 frames (text the skip stops at, choices)
// and a click every 48 (the choices Return does not make: a grid of 3 x 3 places, 28-72% of
// the screen's height, clear of the buttons at its foot, one after another), until a line of
// another scenario shows or every line of this one has, or none new for SCNBOOT_SWEEP_STUCK
// frames (default 9000: 2.5 minutes; a movie playing is not counted); then a "[sweep]" line:
// how it ended, the frames, the lines shown of all, the last, the slowest frame, the heap's peak
bool sweep = Environment.GetEnvironmentVariable("SCNBOOT_SWEEP") == "1";
// (SCNBOOT_SWEEP_CLICKS="x,y;x,y..." clicks there instead; for a game where a place of the grid is a
// button that opens a screen of its own, "safe" leaves out the grid's lower right (Doushite Daite
// Kurenai no!?'s message window has a button there) and adds a row at 40% (its choices are at
// 30-45% of the height, between the grid's rows), and "center" clicks only in the middle:
// tests\sweep.ps1 runs a stuck scenario again so)
// "auto:x,y;x,y..." clicks those places as "auto" clicks its grid (the message window of a game that
// has it below the grid), the buttons sought after them as there
string sweepClicksSetting = Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_CLICKS") ?? "";
bool autoClicks = sweepClicksSetting == "auto" || sweepClicksSetting.StartsWith("auto:");
if (sweepClicksSetting.StartsWith("auto:"))
    sweepClicksSetting = sweepClicksSetting[5..];
host.SweepClicks = sweepClicksSetting switch
{
    "center" => [(vm.ScreenWidth / 2, vm.ScreenHeight / 2)],
    "auto" => [.. from y in new[] { 28, 50, 72 } from x in new[] { 50, 25, 75 } select (vm.ScreenWidth * x / 100, vm.ScreenHeight * y / 100)],
    "safe" => [.. from y in new[] { 28, 40, 50, 72 } from x in new[] { 50, 25, 75 } where !(x == 75 && y == 72)
                  select (vm.ScreenWidth * x / 100, vm.ScreenHeight * y / 100)],
    { Length: > 0 } clicks => clicks.Split(';').Select(c => c.Split(',')).Select(c => (int.Parse(c[0]), int.Parse(c[1]))).ToArray(),
    _ => [.. from y in new[] { 28, 50, 72 } from x in new[] { 50, 25, 75 } select (vm.ScreenWidth * x / 100, vm.ScreenHeight * y / 100)],
};
// SCNBOOT_SWEEP_CLICKS=auto: the grid's clicks until nothing new comes (no new line, no file read
// for the first time) for SCNBOOT_SWEEP_AUTO frames (default 900); then the clicks go where the
// scripts look for the mouse (ClickSeeker): the buttons of the sprite list 04C7 tells apart, and
// the boxes of the places the scripts compare the mouse's x and y with, each once, until
// something new comes
var seeker = autoClicks ? new ClickSeeker(vm.ScreenWidth, vm.ScreenHeight) : null;
int seekAfter = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_AUTO"), out int seekFrames) ? seekFrames : 900;
if (seeker != null)
    vm.CompareSeen = (a, b, cmp) => seeker.Compared(host.MousePosition, a, b, cmp);
int sweepStuck = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_STUCK"), out int stuckFrames) ? stuckFrames : 9000;
string? sweepFile = null, sweepEnd = null, sweepLast = null, slowestAt = null;
// (sweepLastLine: the last new line alone - a movie playing is not "something new" for the clicks sought:
// EDEN V's rooms loop a movie until the place leading out is clicked)
int sweepTotal = 0, sweepLastNew = 0, sweepLastLine = 0, sweepLastBlock = int.MaxValue;
bool sweepReachedEnd = false;
long slowestMs = 0, heapPeak = 0;
var sweepSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
// (a script error the engine passes over, as its reports are off: told, the run goes on)
vm.ScriptErrorPassed = message => Console.WriteLine($"  [script error passed] Frame {host.Frame}: {message}");
if (Environment.GetEnvironmentVariable("SCNBOOT_PLAYFROM") is { } playFrom && playFrom.LastIndexOf(':') is > 0 and int colon)
{
    var scenarios = ScenarioText.ReadScenarios(folder, scheme);
    sweepFile = playFrom[..colon];
    vm.PlayFrom(sweepFile, int.Parse(playFrom[(colon + 1)..]), scenarios.Select(s => s.Name).ToList());
    // Message lines as text.tsv counts them: not empty, not a comment, command or speaker
    if (scenarios.FirstOrDefault(s => s.Name.Equals(sweepFile, StringComparison.OrdinalIgnoreCase)).Data is { } scenario)
    {
        var text = Encoding.GetEncoding(932).GetString(scenario).Split('\n');
        static bool Message(string l) => l.Trim() is { Length: > 0 } t && t[0] is not (';' or '$' or '【');
        sweepTotal = text.Count(Message);
        // The first line (1-based) of the scenario's last message: lines of it shown together are
        // not counted apart, and the game may stand at a screen of its own after it
        int last = Array.FindLastIndex(text, Message);
        if (last >= 0)
        {
            int first = last;
            while (first > 0 && Message(text[first - 1]))
                first--;
            sweepLastBlock = first + 1;
        }
    }
    vm.LineShown += id =>
    {
        Console.WriteLine($"  [line] {id} at {host.Frame}");
        if (!sweep)
            return;
        if (!id.StartsWith(sweepFile + ":", StringComparison.OrdinalIgnoreCase))
            sweepEnd ??= "done (next scenario)";
        else
        {
            sweepLast = id;
            // A later line may repeat an earlier one's text, so the block is kept once reached
            if (int.TryParse(id[(id.LastIndexOf(':') + 1)..], out int line) && line >= sweepLastBlock)
                sweepReachedEnd = true;
            if (sweepSeen.Add(id))
                sweepLastNew = sweepLastLine = host.Frame;
        }
    };
}
if (!vm.LoadModule(0, start, start: true))
{
    Console.WriteLine($"{start} is missing.");
    return 1;
}
// SCNBOOT_GPUCHECK=cases: the GPU kernels against the CPU code (GpuCheck.cs), then the end
if (int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_GPUCHECK"), out int gpuCases))
{
    if (OpenShiina.Gpu.VulkanAccelerator.TryCreate(out string? checkError) is not { } checkGpu)
    {
        Console.WriteLine($"No GPU: {checkError}");
        return 1;
    }
    Console.WriteLine($"GPU: {checkGpu.Name}");
    using (checkGpu)
        return GpuCheck.Run(vm, checkGpu, gpuCases);
}

// SCNBOOT_TRACE="from:to": the time and main-loop rounds of every frame in that range
int traceFrom = int.MaxValue, traceTo = -1;
if (Environment.GetEnvironmentVariable("SCNBOOT_TRACE") is { } trace && trace.Split(':') is [var tf, var tt])
    (traceFrom, traceTo) = (int.Parse(tf), int.Parse(tt));

var wheel = (Environment.GetEnvironmentVariable("SCNBOOT_WHEEL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .ToDictionary(w => int.Parse(w.Split(':')[0]), w => w.EndsWith(":-") ? -120 : 120);
var focus = (Environment.GetEnvironmentVariable("SCNBOOT_FOCUS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .ToDictionary(f => int.Parse(f.Split(':')[0]), f => f.EndsWith(":1"));
// SCNBOOT_HOT="from:to": every instruction run in those frames (slot, module, offset, opcode, times)
int hotFrom = int.MaxValue, hotTo = -1;
if (Environment.GetEnvironmentVariable("SCNBOOT_HOT") is { } hotRange && hotRange.Split(':') is [var hf, var ht])
    (hotFrom, hotTo) = (int.Parse(hf), int.Parse(ht));
int closeAt = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_CLOSE"), out int ca) ? ca : -1;
int frame = 0;
uint pausedFor = 0;
var dumps = (Environment.GetEnvironmentVariable("SCNBOOT_DUMP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(d => d.Split(':', 4)).Where(d => d.Length == 4)
    .Select(d => (Frame: int.Parse(d[0]), Address: Convert.ToInt32(d[1], 16), Length: int.Parse(d[2]), File: d[3])).ToList();
var pokes = (Environment.GetEnvironmentVariable("SCNBOOT_POKE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(p => p.Split('=')).Select(p => (Address: p[0][0] switch
    {
        'g' => vm.GAddress(int.Parse(p[0][1..])),
        'a' => vm.AAddress(int.Parse(p[0][1..])),
        'b' => vm.BAddress(int.Parse(p[0][1..])),
        _ => throw new ArgumentException($"SCNBOOT_POKE: {p[0]}"),
    }, Value: int.Parse(p[1]))).ToList();
byte[]? areaCopy = Environment.GetEnvironmentVariable("SCNBOOT_AREAS") == "1" ? new byte[vm.ScreenWidth * vm.ScreenHeight * 3] : null;
long areaPixels = 0, areaFrames = 0;
int areaMisses = 0;
var areaSizes = new int[10];
if (Environment.GetEnvironmentVariable("SCNBOOT_PAUSE") == "1")
    vm.PauseInBackground = true;
StartWatchdog();
try
{
    var frameTime = new System.Diagnostics.Stopwatch();
    for (; frame < frames; frame++)
    {
        host.Clock = (uint)(frame * 1000L / 60) + pausedFor;
        host.Frame = frame;
        frameTime.Restart();
        // A place for each click of the sweep (they come every 48 frames), while nothing new comes
        if (seeker != null && host.Sweeping && frame % 48 == 0)
        {
            if (frame - Math.Max(sweepLastLine, host.LastNewFile) > seekAfter)
                host.SweepPoint = seeker.Next(vm.HitTargets(), frame);
            else
            {
                seeker.Stop(frame);
                host.SweepPoint = null;
            }
        }
        foreach (var (address, value) in pokes)
            vm.Write32(address, value);
        foreach (var (message, wParam) in host.Messages())
            vm.WindowMessage(message, wParam, message is >= ScnMessage.LButtonDown and <= ScnMessage.MButtonUp
                ? ScnMessage.Point(host.MousePosition.X, host.MousePosition.Y) : ScnMessage.Key(0, message == ScnMessage.KeyDown, false));
        if (frame == hotFrom)
            vm.HotSpots = new();
        if (frame == hotTo + 1 && vm.HotSpots is { } hotSpots)
        {
            vm.HotSpots = null;
            foreach (var ((slot, codeBase, offset, op), times) in hotSpots.OrderBy(h => h.Key.Base).ThenBy(h => h.Key.Offset))
                Console.WriteLine($"  [hot] slot {slot} {vm.DescribeAddress(codeBase + offset)} {op:X4} x{times}");
        }
        if (focus.TryGetValue(frame, out bool active))
        {
            Console.WriteLine($"  [focus] frame {frame}: {(active ? "got" : "lost")}");
            vm.Notify(active ? ScnEvent.Activate : ScnEvent.Deactivate);
            if (!active && vm.Paused && focus.Where(f => f.Key > frame && f.Value).Select(f => f.Key).DefaultIfEmpty(-1).Min() is int back and > 0)
            {
                pausedFor += (uint)((back - frame) * 1000L / 60);
                host.Clock = (uint)(frame * 1000L / 60) + pausedFor;
                focus.Remove(back);
                Console.WriteLine($"  [focus] paused until frame {back}: got");
                vm.Notify(ScnEvent.Activate);
            }
        }
        if (wheel.TryGetValue(frame, out int delta))
            vm.WindowMessage(ScnMessage.MouseWheel, delta << 16, ScnMessage.Point(host.MousePosition.X, host.MousePosition.Y));
        if (frame == closeAt)
        {
            bool closed = vm.CloseWindow();
            Console.WriteLine($"  [close] frame {frame}: the scripts {(closed ? "let the window close" : "keep the window")}");
            if (closed)
            {
                if (pictures != null)
                    SaveScreen(frame);
                break;
            }
        }
        if (frame == profileFrame)
        {
            vm.OpTimes!.Clear();
            vm.NativeTimes.Clear();
        }
        if (!vm.RunFrame())
        {
            Console.WriteLine($"The scripts quit after {frame} frames.");
            break;
        }
        foreach (var d in dumps.Where(d => d.Frame == frame))
            File.WriteAllBytes(d.File, vm.ReadBytes(d.Address, d.Length));
        if (areaCopy != null)
        {
            var area = vm.TakeChangedArea();
            int stride = vm.ScreenWidth * 3;
            for (int y = area.T; y < area.B; y++)
                vm.Window.Slice(y * stride + area.L * 3, area.Width * 3).CopyTo(areaCopy.AsSpan(y * stride + area.L * 3));
            if (!area.IsEmpty)
            {
                areaPixels += (long)area.Width * area.Height;
                areaFrames++;
            }
            if (!area.IsEmpty)
                areaSizes[Math.Min(9, (int)((long)area.Width * area.Height * 10 / (vm.ScreenWidth * vm.ScreenHeight)))]++;
            if (!vm.Window.SequenceEqual(areaCopy))
            {
                int at = vm.Window.CommonPrefixLength(areaCopy);
                if (++areaMisses <= 5)
                    Console.WriteLine($"  [areas] frame {frame}: the copy differs at {at % stride / 3},{at / stride} (area {area})");
                vm.Window.CopyTo(areaCopy);
            }
        }
        if (frame == profileFrame)
        {
            Console.WriteLine($"  [profile] frame {frame}: {frameTime.Elapsed.TotalMilliseconds:F1} ms");
            foreach (var (op, ticks) in vm.OpTimes!.OrderByDescending(t => t.Value).Take(8))
                Console.WriteLine($"  [profile] op {op:X4}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms");
            foreach (var (routine, (ticks, calls)) in vm.NativeTimes.OrderByDescending(t => t.Value.Ticks))
                Console.WriteLine($"  [profile] {routine}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms ({calls} calls)");
        }
        if (frameTime.ElapsedMilliseconds >= 1000)
            Console.WriteLine($"  [slow] frame {frame}: {frameTime.ElapsedMilliseconds} ms, {vm.FrameRounds} rounds");
        // SCNBOOT_HEAP=1: the scripts' memory in use every 60 frames
        if (frame % 60 == 0 && Environment.GetEnvironmentVariable("SCNBOOT_HEAP") == "1")
            Console.WriteLine($"  [heap] frame {frame}: {vm.HeapInUse >> 20} MB{(vm.ShownLine is { } heapLine ? $" at {heapLine}" : "")}");
        heapPeak = Math.Max(heapPeak, vm.HeapInUse);
        if (sweep)
        {
            if (frameTime.ElapsedMilliseconds > slowestMs)
                (slowestMs, slowestAt) = (frameTime.ElapsedMilliseconds, vm.ShownLine ?? $"frame {frame}");
            if (sweepEnd == null && sweepTotal > 0 && sweepSeen.Count >= sweepTotal)
                sweepEnd = "done (every line)";
            if (sweepEnd == null && host.NextModule is { } next)
                sweepEnd = $"done (next module {next})";
            // (a module's story going on reads files for the first time: its pictures, voices)
            if (host.ModuleSwapped)
                sweepLastNew = Math.Max(sweepLastNew, host.LastNewFile);
            // A movie playing (an ending of minutes) is not standing still
            if (vm.MoviePlaying)
                sweepLastNew = frame;
            if (sweepEnd == null && frame - sweepLastNew > sweepStuck)
                // Standing still after the scenario's last message shows is its end
                sweepEnd = sweepReachedEnd ? "done (end)" : "stuck";
            if (sweepEnd != null)
                break;
        }
        else if (frame >= traceFrom && frame <= traceTo)
            Console.WriteLine($"  [frame] {frame}: {frameTime.Elapsed.TotalMilliseconds:F2} ms, {vm.FrameRounds} rounds");
        if (pictures != null && frame % every == 0)
            SaveScreen(frame);
    }
    if (frame == frames)
        Console.WriteLine($"Still running after {frames} frames.");
    foreach (string choice in vm.GpuChoices)
        Console.WriteLine($"  [gpu] chose {choice}");
}
catch (ScnException ex)
{
    Console.WriteLine($"Frame {frame}: {ex.Message}");
    if (ex.Message.StartsWith("Out of script memory", StringComparison.Ordinal))
        Console.WriteLine($"  [heap] {vm.HeapReport()}");
    // (an operand the decoder cannot read names no slot: the task running then)
    var c = ex.Slot >= 0 ? vm.Slot(ex.Slot) : vm.CurrentTask;
    if (ex.Slot < 0 && c != null)
        Console.WriteLine($"  slot {c.Slot} decoding at {c.Pc:X8} ({vm.DescribeAddress(c.Pc)})");
    if (c != null)
    {
        try
        {
            var ins = vm.Decode(c, ex.Address);
            Console.WriteLine($"  instruction {ins.Op:X4}, {ins.Args.Length} operands, raw [{string.Join(", ", ins.Raw)}]");
        }
        catch (ScnException)
        {
        }
        Console.WriteLine($"  slot {c.Slot}: module at {c.CodeBase:X8}, running code at {c.Base:X8}, offset {ex.Address - c.Base:X5}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Frame {frame}: {vm.CrashReport(ex)}");
}

if (sweep)
    Console.WriteLine($"[sweep]\t{sweepFile}\t{sweepEnd ?? (frame >= frames ? "out of frames" : "stopped")}\t{frame}\t{sweepSeen.Count}/{sweepTotal}\t{sweepLast}\t{slowestMs} ms at {slowestAt}\theap peak {heapPeak >> 20} MB");
if (pictures != null)
    SaveScreen(frame);
if (areaCopy != null)
    Console.WriteLine($"  [areas] tenths of the screen: {string.Join(" ", areaSizes)}; {areaMisses} frames missed; {areaFrames} frames changed, {(areaFrames == 0 ? 0 : areaPixels / areaFrames)} pixels a changed frame on average ({vm.ScreenWidth * vm.ScreenHeight} in all)");
Console.WriteLine($"gCPUID = {vm.Read32(vm.GlobalAddress("gCPUID")):X}, x86 instructions run: {vm.Cpu.Executed}");
if (ScnVm.AlphaVectors.Length > 0)
    Console.WriteLine("  " + ScnVm.AlphaVectors);
Console.WriteLine($"  file cache {vm.FileCacheBytes >> 20} MB (memory {FileCachePresets.TotalMemory() >> 20} MB)");
if (vm.LoadInBackground)
{
    Console.WriteLine($"  read ahead: {vm.ReadAheadCounts.Read} files, {vm.ReadAheadCounts.Used} of them used");
    Console.WriteLine($"  shared files read: {vm.SharedCounts.Read} ({vm.SharedCounts.Bytes >> 10} KB)");
}
foreach (string read in vm.SlowReads.Take(12))
    Console.WriteLine($"  slow file read: {read}");
if (vm.OpTimes != null)
    foreach (var (op, ticks) in vm.OpTimes.OrderByDescending(t => t.Value).Take(12))
        Console.WriteLine($"  op {op:X4}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms ({vm.OpCounts[op]} runs)");
foreach (var (routine, (ticks, calls)) in vm.NativeTimes.OrderByDescending(t => t.Value.Ticks))
    Console.WriteLine($"  C# {routine}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms ({calls} calls)");
foreach (var (entry, p) in vm.Cpu.Profile.OrderByDescending(p => p.Value.Instructions).Take(8))
    Console.WriteLine($"  x86 {entry:X8}: {p.Calls} calls, {p.Instructions} instructions");
foreach (var r in vm.RoutineReport().OrderBy(r => r.Address))
    Console.WriteLine($"  routine {r.Address:X8} {r.Signature}: " +
        (r.Native != null ? $"C# {r.Native}" : r.Translated ? "translated" : $"interpreter{(r.NotTranslated != null ? $" ({r.NotTranslated})" : "")}"));
Console.WriteLine($"Opcodes run ({vm.OpCounts.Count} kinds): " +
    string.Join(" ", vm.OpCounts.OrderBy(k => k.Key).Select(k => $"{k.Key:X4}x{k.Value}")));
return 0;

// Stall detection: when one frame runs longer than 5 s, counts the instructions run per script
// address and prints where the time goes every SCNBOOT_STALL_REPORT seconds (default 15); after
// SCNBOOT_STALL seconds (default 60) on one frame it saves the screen and stops the run.
void StartWatchdog()
{
    int limit = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_STALL"), out var s) ? s : 60;
    int report = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_STALL_REPORT"), out var r) ? r : 15;
    var thread = new Thread(() =>
    {
        int last = -1;
        var since = System.Diagnostics.Stopwatch.StartNew();
        long nextReport = 0;
        while (true)
        {
            Thread.Sleep(500);
            int current = host.Frame;
            if (current != last)
            {
                last = current;
                since.Restart();
                vm.HotSpots = null;
                nextReport = 5;
                continue;
            }
            double seconds = since.Elapsed.TotalSeconds;
            if (seconds < nextReport)
                continue;
            if (vm.HotSpots == null)
            {
                vm.HotSpots = new();
                nextReport = 5 + report;
                Console.WriteLine($"  [stall] frame {current} has run {seconds:F0} s ({vm.FrameRounds} rounds); counting where");
                continue;
            }
            nextReport = (long)seconds + report;
            PrintStall(current, seconds);
            if (seconds >= limit)
            {
                Console.WriteLine($"  [stall] stopping: frame {current} ran over {limit} s (SCNBOOT_STALL)");
                if (pictures != null)
                    SaveScreen(current);
                Console.Out.Flush();
                Environment.Exit(3);
            }
        }
    }) { IsBackground = true, Name = "stall watchdog" };
    thread.Start();
}

void PrintStall(int current, double seconds)
{
    var hot = vm.HotSpots;
    if (hot == null)
        return;
    KeyValuePair<(int Slot, int Base, int Offset, int Op), int>[] top;
    long total;
    lock (hot)
    {
        top = hot.OrderByDescending(h => h.Value).Take(10).ToArray();
        total = hot.Values.Sum(v => (long)v);
    }
    var task = vm.CurrentTask;
    Console.WriteLine($"  [stall] frame {current}: {seconds:F0} s, {vm.FrameRounds} rounds, {total} instructions counted; " +
                      $"now slot {task?.Slot} at {(task == null ? 0 : task.Current - task.Base):X5}, x86 run so far {vm.Cpu.Executed}");
    foreach (var ((slot, codeBase, offset, op), n) in top)
        Console.WriteLine($"    slot {slot,3} code {codeBase:X8} +{offset:X5}: {n,10} op {op:X4}");
}

// The surface the window shows, as the player would see it (and the others with SCNBOOT_SURFACES=1,2,..)
void SaveScreen(int n)
{
    // The window's picture (what WM_PAINT has put there), then any surfaces asked for
    var window = new OpenShiina.Formats.PixelImage(vm.ScreenWidth, vm.ScreenHeight, OpenShiina.Formats.PixelLayout.Bgr24, vm.Window.ToArray());
    File.WriteAllBytes(Path.Combine(pictures!, $"frame_{n:D4}.png"), OpenShiina.Formats.PngEncoder.Encode(window));
    foreach (var extra in (Environment.GetEnvironmentVariable("SCNBOOT_SURFACES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        SaveSurface(int.Parse(extra), $"frame_{n:D4}_s{extra}.png");
}

void SaveSurface(int surface, string file)
{
    int pixels = vm.SurfaceField(surface, 2);
    int w = vm.SurfaceField(surface, 7), h = vm.SurfaceField(surface, 8), pitch = vm.SurfaceField(surface, 10);
    if (pixels == 0 || w <= 0 || h <= 0)
    {
        Console.WriteLine($"  [surface {surface} has no picture: {string.Join(" ", Enumerable.Range(0, 11).Select(f => vm.SurfaceField(surface, f).ToString("X")))}]");
        return;
    }
    var rgb = new byte[w * h * 3];
    for (int y = 0; y < h; y++)
        vm.ReadBytes(pixels + y * pitch, rgb.AsSpan(y * w * 3, w * 3));
    var image = new OpenShiina.Formats.PixelImage(w, h, OpenShiina.Formats.PixelLayout.Bgr24, rgb);
    File.WriteAllBytes(Path.Combine(pictures!, file), OpenShiina.Formats.PngEncoder.Encode(image));
}

sealed class Host(GameData data, string saveFolder) : IScnHost
{
    public string SaveFolder => saveFolder;

    public byte[]? ReadSaveFile(string name) =>
        File.Exists(Path.Combine(saveFolder, name)) ? File.ReadAllBytes(Path.Combine(saveFolder, name)) : null;

    public void WriteSaveFile(string name, byte[] bytes)
    {
        Console.WriteLine($"  [write] {name} ({bytes.Length} bytes)");
        File.WriteAllBytes(Path.Combine(saveFolder, name), bytes);
    }

    public void DeleteSaveFile(string name) => File.Delete(Path.Combine(saveFolder, name));

    public int MessageBox(string text, string caption, int type)
    {
        Console.WriteLine($"  [message box {type}] {text}");
        return (type & 0xF) is 4 or 3 ? 6 : 1;    // yes / OK
    }

    /// <summary>A clock that moves 1/60 s a frame, so that runs are the same every time.</summary>
    public uint Clock { get; set; }

    // SCNBOOT_FILE="name=file;...": a file of the game's archives read from a file of the disk
    // instead (a test scenario in place of one of the game's); the game folder is not touched
    private static readonly (string Name, string File)[] s_swaps = (Environment.GetEnvironmentVariable("SCNBOOT_FILE") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToArray();

    private static string? Swap(string name) =>
        s_swaps.FirstOrDefault(s => name.EndsWith(s.Name, StringComparison.OrdinalIgnoreCase)).File;

    // SCNBOOT_FILES=1: every file of the archives the scripts read, with its size and the time to
    // decode it (to find files read over and over)
    private static readonly bool s_traceFiles = Environment.GetEnvironmentVariable("SCNBOOT_FILES") == "1";

    // The files read so far, and the frame of the last one read for the first time (the story
    // going on, for SCNBOOT_SWEEP_CLICKS=auto: a game standing at a menu reads nothing new)
    private readonly HashSet<string> m_filesRead = new(StringComparer.OrdinalIgnoreCase);

    public int LastNewFile { get; private set; }

    // SCNBOOT_MODULE="first.scn=module.scn": the first time the scripts read the module their story
    // starts with, another is read in its place (a story of SCN modules swept module by module, the
    // game started from its title as it is, the variables its menus set kept); NextModule is the
    // first module read after it (with SCNBOOT_SWEEP=1 the run ends there: "done (next module)")
    private static readonly string[] s_module = (Environment.GetEnvironmentVariable("SCNBOOT_MODULE") ?? "").Split('=', 2);

    // SCNBOOT_MODULE_SUB="item,tips": modules whose names start so are called and come back (Deco's
    // items and tips): reading one is not the next module
    private static readonly string[] s_subModules = (Environment.GetEnvironmentVariable("SCNBOOT_MODULE_SUB") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries);

    public bool ModuleSwapped { get; private set; }

    public string? NextModule { get; private set; }

    public byte[]? ReadFile(string name)
    {
        if (s_module.Length == 2 && name.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
        {
            if (!ModuleSwapped && name.EndsWith(s_module[0], StringComparison.OrdinalIgnoreCase))
            {
                ModuleSwapped = true;
                name = name[..^s_module[0].Length] + s_module[1];
                Console.WriteLine($"[module] {name} in place of {s_module[0]} at {Frame}");
            }
            else if (ModuleSwapped && NextModule == null && !name.EndsWith(s_module[1], StringComparison.OrdinalIgnoreCase)
                && !s_subModules.Any(s => Path.GetFileName(name).StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                NextModule = name;
        }
        if (m_filesRead.Add(name))
            LastNewFile = Frame;
        if (Swap(name) is { } swap)
            return File.ReadAllBytes(swap);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var bytes = data.Read(name.Replace('/', '\\'));
        if (s_traceFiles)
            Console.WriteLine($"[file] {name} {bytes?.Length ?? -1} {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms at {Frame}");
        return bytes;
    }
    public long? ArchiveFileSize(string name) => Swap(name) is { } swap ? new FileInfo(swap).Length : data.Size(name.Replace('/', '\\'));
    public long? ArchiveFileSize(string archive, string name) => data.Size(archive, name.Replace('/', '\\'));
    public IReadOnlyList<string> ArchiveFileNames(string extension) => data.Names(extension);
    public bool MountArchive(string archive, bool mounted) => data.Mount(archive, mounted);
    public byte[]? ExecutableCopy(string name) => data.ExecutableCopy(LooseFileSize(name));

    public long? LooseFileSize(string name) =>
        name.Equals("RIO.INI", StringComparison.OrdinalIgnoreCase) ? ReadLooseFile(name)?.Length
        : data.LooseFile(name) is { } path ? new FileInfo(path).Length : null;

    public byte[]? ReadLooseFile(string name)
    {
        var bytes = data.LooseFile(name) is { } path ? File.ReadAllBytes(path) : null;
        // (with SCNBOOT_FILES=1 the files of the game's folder too: EDEN's movies)
        if (s_traceFiles && !name.Equals("RIO.INI", StringComparison.OrdinalIgnoreCase))
        {
            if (m_filesRead.Add(name))
                LastNewFile = Frame;
            Console.WriteLine($"[loose] {name} {bytes?.Length ?? -1} at {Frame}");
        }
        // SCNBOOT_INI="Key=value;...": RIO.INI as the scripts see it, with those keys changed
        if (bytes != null && name.Equals("RIO.INI", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("SCNBOOT_INI") is { Length: > 0 } changes)
        {
            var sjis = Encoding.GetEncoding(932);
            string text = sjis.GetString(bytes);
            foreach (var change in changes.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string key = change.Split('=')[0];
                var pattern = new System.Text.RegularExpressions.Regex($@"(?im)^{System.Text.RegularExpressions.Regex.Escape(key)}=[^\r\n]*");
                // (a key the file lacks goes in after its first section's name: Raillore's Opt_RealMemorySize_Free)
                text = pattern.IsMatch(text) ? pattern.Replace(text, change)
                    : new System.Text.RegularExpressions.Regex(@"(?m)^\[[^\]\r\n]*\][^\r\n]*\r?\n").Replace(text, m => m.Value + change + "\r\n", 1);
            }
            bytes = sjis.GetBytes(text);
        }
        return bytes;
    }

    public uint Milliseconds => Clock;

    public int Frame { get; set; }

    // SCNBOOT_PRESS: (first frame, last frame, virtual key)
    private readonly List<(int From, int To, int Key)> m_presses =
        (Environment.GetEnvironmentVariable("SCNBOOT_PRESS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split(':'))
            .Select(p => (int.Parse(p[0]), int.Parse(p[0]) + (p.Length > 2 ? int.Parse(p[2]) : 3) - 1,
                Convert.ToInt32(p[1], p[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)))
            .ToList();

    // SCNBOOT_SWEEP: Ctrl held but for frames 12-35 of every 48 (a game may not let a choice be
    // made while it skips: Ero-On), Return down 2 frames of every 12; SCNBOOT_SWEEP_FROM=frame:
    // from that frame on (before it SCNBOOT_MOUSE's clicks, as a title's START)
    private readonly bool m_sweep = Environment.GetEnvironmentVariable("SCNBOOT_SWEEP") == "1";
    private readonly int m_sweepFrom = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_FROM"), out int from) ? from : 0;

    // SCNBOOT_SWEEP_UNTIL=frame: the sweep's keys and clicks stop there (a scene reached, then
    // SCNBOOT_PRESS / SCNBOOT_MOUSE alone: which key ends a game's opening movie)
    private readonly int m_sweepUntil = int.TryParse(Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_UNTIL"), out int until) ? until : int.MaxValue;

    public bool Sweeping => m_sweep && Frame >= m_sweepFrom && Frame < m_sweepUntil;

    private bool SweepCtrl => Sweeping && Frame % 48 is < 12 or >= 36;

    // SCNBOOT_SWEEP_RETURN=0: no Return (a menu whose Return takes the item it last showed, not the
    // one under the mouse: Tekoire's info menu); the clicks alone choose and go on
    private readonly bool m_sweepReturn = Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_RETURN") != "0";

    private bool SweepReturn => Sweeping && m_sweepReturn;

    public bool KeyDown(int virtualKey) =>
        virtualKey is 0x11 or 0xA2 && SweepCtrl || SweepReturn && virtualKey == 0x0D && Frame % 12 < 2
        || m_presses.Any(p => p.Key == virtualKey && Frame >= p.From && Frame <= p.To);

    // SCNBOOT_MOUSE: (first frame, x, y, buttons, last frame the buttons are held)
    private readonly List<(int From, int X, int Y, int Buttons, int To)> m_mouse =
        (Environment.GetEnvironmentVariable("SCNBOOT_MOUSE") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split(':'))
            .Select(p =>
            {
                int from = int.Parse(p[0]);
                var xy = p[1].Split(',');
                int buttons = p.Length > 2 ? int.Parse(p[2]) : 0;
                return (from, int.Parse(xy[0]), int.Parse(xy[1]), buttons, from + (p.Length > 3 ? int.Parse(p[3]) : 3) - 1);
            })
            .OrderBy(m => m.Item1)
            .ToList();

    /// <summary>
    /// SCNBOOT_SWEEP: where the mouse clicks, 2 frames of every 48, a place after another (the
    /// choices: Ero-On's under the top, Yuru Plus's heroines left and right, the menu Maki Fes!
    /// shows after a scenario); text goes on too.
    /// </summary>
    public (int X, int Y)[] SweepClicks { get; set; } = [(0, 0)];

    private bool SweepClicking => Sweeping && Frame % 48 is 24 or 25;

    // SCNBOOT_SWEEP_SHUFFLE=1: the places in an order of their own (a hash of the click's number),
    // not one after another: a menu that comes back at the same frame of the 48 after each of its
    // choices (Tekoire's info menu) would be clicked at the same place each time
    private readonly bool m_sweepShuffle = Environment.GetEnvironmentVariable("SCNBOOT_SWEEP_SHUFFLE") == "1";

    /// <summary>SCNBOOT_SWEEP_CLICKS=auto: the place ClickSeeker chose, used instead of the grid's.</summary>
    public (int X, int Y)? SweepPoint { get; set; }

    public (int X, int Y) MousePosition
    {
        get
        {
            if (Sweeping && SweepPoint is { } point)
                return point;
            if (Sweeping)
                return SweepClicks[m_sweepShuffle ? (int)((uint)(Frame / 48) * 2654435761u >> 16) % SweepClicks.Length
                    : Frame / 48 % SweepClicks.Length];
            var started = m_mouse.Where(m => m.From <= Frame).ToList();
            return started.Count > 0 ? (started[^1].X, started[^1].Y) : (0, 0);
        }
    }

    public int MouseButtons => SweepClicking ? 1 : m_mouse.Where(m => m.From <= Frame && Frame <= m.To).Select(m => m.Buttons).FirstOrDefault();

    /// <summary>The window messages of this frame: keys and buttons that go down or up.</summary>
    public IEnumerable<(int Message, int WParam)> Messages()
    {
        if (m_sweep && Frame == m_sweepUntil)
            yield return (ScnMessage.KeyUp, 0x11);
        if (Sweeping)
        {
            if (Frame == m_sweepFrom || Frame % 48 == 36)
                yield return (ScnMessage.KeyDown, 0x11);
            if (Frame % 48 == 12)
                yield return (ScnMessage.KeyUp, 0x11);
            if (m_sweepReturn && Frame % 12 == 0)
                yield return (ScnMessage.KeyDown, 0x0D);
            if (m_sweepReturn && Frame % 12 == 2)
                yield return (ScnMessage.KeyUp, 0x0D);
            if (Frame % 48 == 24)
                yield return (ScnMessage.LButtonDown, 1);
            if (Frame % 48 == 26)
                yield return (ScnMessage.LButtonUp, 0);
        }
        foreach (var p in m_presses)
        {
            if (p.From == Frame)
                yield return (ScnMessage.KeyDown, p.Key);
            if (p.To + 1 == Frame)
                yield return (ScnMessage.KeyUp, p.Key);
        }
        foreach (var m in m_mouse)
            foreach (var (bit, down, up) in new[] { (1, ScnMessage.LButtonDown, ScnMessage.LButtonUp), (2, ScnMessage.RButtonDown, ScnMessage.RButtonUp), (4, ScnMessage.MButtonDown, ScnMessage.MButtonUp) })
            {
                if ((m.Buttons & bit) == 0)
                    continue;
                if (m.From == Frame)
                    yield return (down, bit);
                if (m.To + 1 == Frame)
                    yield return (up, 0);
            }
    }

    // SCNBOOT_JOY: (first frame, last frame, the joystick then); in the middle at other times
    private readonly List<(int From, int To, ScnJoystick Joystick)> m_joystick =
        (Environment.GetEnvironmentVariable("SCNBOOT_JOY") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split(':'))
            .Select(p =>
            {
                int from = int.Parse(p[0]);
                var xy = p[1].Split(',');
                return (from, from + (p.Length > 3 ? int.Parse(p[3]) : 3) - 1,
                    new ScnJoystick(int.Parse(xy[0]), int.Parse(xy[1]), p.Length > 2 ? int.Parse(p[2]) : 0));
            })
            .ToList();

    public ScnJoystick? Joystick =>
        m_joystick.Count == 0 ? null
        : m_joystick.Where(j => j.From <= Frame && Frame <= j.To).Select(j => (ScnJoystick?)j.Joystick).FirstOrDefault() ?? new ScnJoystick(32767, 32767, 0);

    public void SetTitle(string title) => Console.WriteLine($"  [title] {title}");

    public void SetFullScreen(bool fullScreen) => Console.WriteLine($"  [full screen] {fullScreen}");

    public IScnMusic? Music => m_music ??= new TestMusic(() => Clock);
    private TestMusic? m_music;

    public IScnFonts? Fonts { get; } = OperatingSystem.IsWindows() ? new OpenShiina.Platform.GdiFonts() : null;
    public IScnShapes? Shapes { get; } = OperatingSystem.IsWindows() ? new OpenShiina.Platform.GdiShapes() : null;
}

// Music that is only decoded, to check what the scripts open: prints the format and length
sealed class TestMusic(Func<uint> clock) : IScnMusic
{
    private int m_next = 1;

    public int Open(byte[] file)
    {
        uint magic = BitConverter.ToUInt32(file, 0);
        byte[]? ogg = (magic & 0xFFFFFF) == 0x56474F ? OpenShiina.Formats.AudioDecoder.DecodeOgv(file) : magic == 0x5367674F ? file : null;
        // (a PAD, the old games' music, is a WAVE as the players decode it)
        byte[]? riff = (magic & 0xFFFFFF) == 0x444150 ? OpenShiina.Formats.AudioDecoder.DecodePad(file) : magic == 0x46464952 ? file : null;
        if (ogg == null && riff != null)
        {
            using var wave = new NAudio.Wave.WaveFileReader(new MemoryStream(riff));
            Console.WriteLine($"  [music] {file.Length} bytes, WAVE: {wave.WaveFormat.SampleRate} Hz, {wave.WaveFormat.Channels} ch, {wave.TotalTime.TotalSeconds:F1} s");
            m_lengths[m_next] = wave.TotalTime.TotalMilliseconds;
            return m_next++;
        }
        if (ogg == null)
        {
            Console.WriteLine($"  [music] {file.Length} bytes, not Ogg (starts {Convert.ToHexString(file, 0, Math.Min(16, file.Length))})");
            return m_next++;
        }
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        Console.WriteLine($"  [music] {file.Length} bytes: {reader.SampleRate} Hz, {reader.Channels} ch, {reader.TotalTime.TotalSeconds:F1} s");
        m_lengths[m_next] = reader.TotalTime.TotalMilliseconds;
        return m_next++;
    }

    // A stream's length (ms) and when it was played, not looping (the run's clock): it plays until
    // then (a staff roll that waits for its music to end: Calendar Girl's)
    private readonly Dictionary<int, double> m_lengths = new();
    private readonly Dictionary<int, uint> m_playedAt = new();

    public void Close(int stream) => Console.WriteLine($"  [music] close {stream}");
    public void Play(int stream, bool loop)
    {
        Console.WriteLine($"  [music] play {stream}{(loop ? " looping" : "")}");
        if (loop)
            m_playedAt.Remove(stream);
        else
            m_playedAt[stream] = clock();
    }
    public void Stop(int stream) { }
    public void Pause(int stream) => Console.WriteLine($"  [music] pause {stream}");
    public void Resume(int stream) => Console.WriteLine($"  [music] resume {stream}");
    public void SetVolume(int stream, int volume) => Console.WriteLine($"  [music] volume {stream} {volume}");
    public bool IsPlaying(int stream) =>
        !m_playedAt.TryGetValue(stream, out uint at) || !m_lengths.TryGetValue(stream, out double ms) || clock() - at < ms;
}
