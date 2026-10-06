// Boots a game's SCN scripts in ScnVm without a screen, the way the engine starts (RIO.INI
// "Scn=start.scn" into slot 0), and runs frames until the scripts reach an opcode the
// interpreter does not run yet. Prints that opcode, where it is, and the opcodes run so far.
//
//   scnboot [game folder] [frames] [picture folder] [every n frames]
// Time runs at 60 frames a second whatever the speed of the run. SCNBOOT_PRESS="frame:vk[:frames],..."
// holds virtual keys down (default 3 frames), e.g. "700:0x0D" presses Return at frame 700.
// SCNBOOT_MOUSE="frame:x,y[:buttons[:frames]];..." puts the mouse at (x, y) from that frame on and
// holds buttons (1 left, 2 right, 4 middle; default none) for some frames (default 3).
// With a picture folder, the surface the window shows is saved as frame_NNNN.png every n frames
// (default 10) and when the run ends.
// The game folder defaults to games\GrandCross\俺妹プラス in a folder above this program.

using System.Text;
using OpenShiina.Archives;
using OpenShiina.Scripting;
using OpenShiina.Story;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string? folder = args.Length > 0 && args[0].Length > 0 ? args[0] : null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory); folder == null && d != null; d = d.Parent)
    if (Directory.Exists(Path.Combine(d.FullName, "games", "GrandCross", "俺妹プラス")))
        folder = Path.Combine(d.FullName, "games", "GrandCross", "俺妹プラス");
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
string saves = Path.Combine(Path.GetTempPath(), "openshiina-scnboot", scheme.Name);
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
if (Environment.GetEnvironmentVariable("SCNBOOT_PROFILE") == "1")
    vm.OpTimes = new();
if (!vm.LoadModule(0, start, start: true))
{
    Console.WriteLine($"{start} is missing.");
    return 1;
}

int frame = 0;
try
{
    for (; frame < frames; frame++)
    {
        host.Clock = (uint)(frame * 1000L / 60);
        host.Frame = frame;
        if (!vm.RunFrame())
        {
            Console.WriteLine($"The scripts quit after {frame} frames.");
            break;
        }
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
    Console.WriteLine($"Frame {frame}: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

if (pictures != null)
    SaveScreen(frame);
Console.WriteLine($"gCPUID = {vm.Read32(vm.GlobalAddress("gCPUID")):X}, x86 instructions run: {vm.Cpu.Executed}");
if (vm.OpTimes != null)
    foreach (var (op, ticks) in vm.OpTimes.OrderByDescending(t => t.Value).Take(12))
        Console.WriteLine($"  op {op:X4}: {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0} ms ({vm.OpCounts[op]} runs)");
foreach (var (entry, p) in vm.Cpu.Profile.OrderByDescending(p => p.Value.Instructions).Take(8))
    Console.WriteLine($"  x86 {entry:X8}: {p.Calls} calls, {p.Instructions} instructions");
Console.WriteLine($"Opcodes run ({vm.OpCounts.Count} kinds): " +
    string.Join(" ", vm.OpCounts.OrderBy(k => k.Key).Select(k => $"{k.Key:X4}x{k.Value}")));
return 0;

// The surface the window shows, as the player would see it (and the others with SCNBOOT_SURFACES=1,2,..)
void SaveScreen(int n)
{
    SaveSurface(vm.DisplaySurface, $"frame_{n:D4}.png");
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

    public byte[]? ReadLooseFile(string name) => data.LooseFile(name) is { } path ? File.ReadAllBytes(path) : null;

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

    public void SetTitle(string title) => Console.WriteLine($"  [title] {title}");

    public void SetFullScreen(bool fullScreen) => Console.WriteLine($"  [full screen] {fullScreen}");

    public IScnMusic? Music { get; } = new TestMusic();

    public IScnFonts? Fonts { get; } = OperatingSystem.IsWindows() ? new OpenShiina.Platform.GdiFonts() : null;
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
    public void Pause(int stream) { }
    public void Resume(int stream) { }
    public void SetVolume(int stream, int volume) => Console.WriteLine($"  [music] volume {stream} {volume}");
    public bool IsPlaying(int stream) => true;
}
