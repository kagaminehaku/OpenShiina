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
// SCNBOOT_FOCUS="frame:0|1,..." has the window lose (0) or get (1) the focus at those frames.
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
var ini = File.ReadAllText(Path.Combine(folder, "RIO.INI"), Encoding.GetEncoding(932));
string version = System.Text.RegularExpressions.Regex.Match(ini, @"v(\d+\.\d+)").Groups[1].Value;
string start = System.Text.RegularExpressions.Regex.Match(ini, @"(?im)^Scn=(.+)$").Groups[1].Value.Trim();
var formats = FormatManager.Instance;
var scheme = formats.LookupGame(Directory.GetFiles(folder, "*.war").First()) is { } name ? formats.GetScheme(name) : null;
if (scheme == null)
{
    Console.WriteLine("The game was not recognised.");
    return 1;
}
using var data = GameData.Open(folder, scheme);
Console.WriteLine($"{scheme.Name}, engine v{version}, first script {start}");

// Save data of the test run: a temporary folder, never the player's real data
string saves = Path.Combine(Path.GetTempPath(), "openshiina-scnboot", PlayerFolders.FolderName(scheme.Name));
Directory.CreateDirectory(saves);
var host = new Host(data, saves);
var vm = new ScnVm(ScnOpcodes.ForVersion(version), host)
{
    EngineVersion = (int)Math.Round(double.Parse(version, System.Globalization.CultureInfo.InvariantCulture) * 100),
};
foreach (var (key, set) in new (string, Action<int>)[] { ("WindowWidth", v => vm.ScreenWidth = v), ("WindowHeight", v => vm.ScreenHeight = v) })
    if (System.Text.RegularExpressions.Regex.Match(ini, $@"(?im)^{key}=(\d+)") is { Success: true } m)
        set(int.Parse(m.Groups[1].Value));
vm.VerifyNatives = Environment.GetEnvironmentVariable("SCNBOOT_VERIFY_NATIVE") == "1";
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
if (!vm.LoadModule(0, start, start: true))
{
    Console.WriteLine($"{start} is missing.");
    return 1;
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
StartWatchdog();
try
{
    var frameTime = new System.Diagnostics.Stopwatch();
    for (; frame < frames; frame++)
    {
        host.Clock = (uint)(frame * 1000L / 60);
        host.Frame = frame;
        frameTime.Restart();
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
        else if (frame >= traceFrom && frame <= traceTo)
            Console.WriteLine($"  [frame] {frame}: {frameTime.Elapsed.TotalMilliseconds:F2} ms, {vm.FrameRounds} rounds");
        if (pictures != null && frame % every == 0)
            SaveScreen(frame);
    }
    if (frame == frames)
        Console.WriteLine($"Still running after {frames} frames.");
}
catch (ScnException ex)
{
    Console.WriteLine($"Frame {frame}: {ex.Message}");
    var c = ex.Slot >= 0 ? vm.Slot(ex.Slot) : null;
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

if (pictures != null)
    SaveScreen(frame);
Console.WriteLine($"gCPUID = {vm.Read32(vm.GlobalAddress("gCPUID")):X}, x86 instructions run: {vm.Cpu.Executed}");
if (vm.OpTimes != null)
    foreach (var (op, ticks) in vm.OpTimes.OrderByDescending(t => t.Value).Take(12))
        Console.WriteLine($"  op {op:X4}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms ({vm.OpCounts[op]} runs)");
foreach (var (routine, (ticks, calls)) in vm.NativeTimes.OrderByDescending(t => t.Value.Ticks))
    Console.WriteLine($"  C# {routine}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms ({calls} calls)");
foreach (var (entry, p) in vm.Cpu.Profile.OrderByDescending(p => p.Value.Instructions).Take(8))
    Console.WriteLine($"  x86 {entry:X8}: {p.Calls} calls, {p.Instructions} instructions");
foreach (var r in vm.RoutineReport().OrderBy(r => r.Address))
    Console.WriteLine($"  routine {r.Address:X8} {r.Signature[..12]}: " +
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

    public byte[]? ReadFile(string name) => data.Read(name.Replace('/', '\\'));
    public long? ArchiveFileSize(string name) => data.Size(name.Replace('/', '\\'));

    public byte[]? ReadLooseFile(string name)
    {
        var bytes = data.LooseFile(name) is { } path ? File.ReadAllBytes(path) : null;
        // SCNBOOT_INI="Key=value;...": RIO.INI as the scripts see it, with those keys changed
        if (bytes != null && name.Equals("RIO.INI", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("SCNBOOT_INI") is { Length: > 0 } changes)
        {
            var sjis = Encoding.GetEncoding(932);
            string text = sjis.GetString(bytes);
            foreach (var change in changes.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string key = change.Split('=')[0];
                text = System.Text.RegularExpressions.Regex.Replace(text, $@"(?im)^{System.Text.RegularExpressions.Regex.Escape(key)}=[^\r\n]*", change);
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

    public bool KeyDown(int virtualKey) => m_presses.Any(p => p.Key == virtualKey && Frame >= p.From && Frame <= p.To);

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

    public (int X, int Y) MousePosition
    {
        get
        {
            var started = m_mouse.Where(m => m.From <= Frame).ToList();
            return started.Count > 0 ? (started[^1].X, started[^1].Y) : (0, 0);
        }
    }

    public int MouseButtons => m_mouse.Where(m => m.From <= Frame && Frame <= m.To).Select(m => m.Buttons).FirstOrDefault();

    /// <summary>The window messages of this frame: keys and buttons that go down or up.</summary>
    public IEnumerable<(int Message, int WParam)> Messages()
    {
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

    public IScnMusic? Music { get; } = new TestMusic();

    public IScnFonts? Fonts { get; } = OperatingSystem.IsWindows() ? new OpenShiina.Platform.GdiFonts() : null;
    public IScnShapes? Shapes { get; } = OperatingSystem.IsWindows() ? new OpenShiina.Platform.GdiShapes() : null;
}

// Music that is only decoded, to check what the scripts open: prints the format and length
sealed class TestMusic : IScnMusic
{
    private int m_next = 1;

    public int Open(byte[] file)
    {
        uint magic = BitConverter.ToUInt32(file, 0);
        byte[]? ogg = (magic & 0xFFFFFF) == 0x56474F ? OpenShiina.Formats.AudioDecoder.DecodeOgv(file) : magic == 0x5367674F ? file : null;
        if (ogg == null)
        {
            Console.WriteLine($"  [music] {file.Length} bytes, not Ogg");
            return m_next++;
        }
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        Console.WriteLine($"  [music] {file.Length} bytes: {reader.SampleRate} Hz, {reader.Channels} ch, {reader.TotalTime.TotalSeconds:F1} s");
        return m_next++;
    }

    public void Close(int stream) => Console.WriteLine($"  [music] close {stream}");
    public void Play(int stream, bool loop) => Console.WriteLine($"  [music] play {stream}{(loop ? " looping" : "")}");
    public void Stop(int stream) { }
    public void Pause(int stream) => Console.WriteLine($"  [music] pause {stream}");
    public void Resume(int stream) => Console.WriteLine($"  [music] resume {stream}");
    public void SetVolume(int stream, int volume) => Console.WriteLine($"  [music] volume {stream} {volume}");
    public bool IsPlaying(int stream) => true;
}
