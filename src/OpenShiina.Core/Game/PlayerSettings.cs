// The players' settings: settings.json in the player folder (PlayerFolders.Root), shared by both
// players, each counting from the next game started: where the interpreter's pixel work runs
// (the CPU or the GPU through Vulkan, OpenShiina.Gpu), how often it runs a frame (the game's pace
// or the screen's), the game controller, the frame rate, and
// for bug reports perf.log, the x86 translation and draw-trace.log. The environment variables
// of earlier versions (OPENSHIINA_GPU, _JOYPAD, _PERF, _X86JIT, _TRACE) still win over them,
// for tests.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenShiina.Scripting;

namespace OpenShiina.Game;

public enum Renderer
{
    /// <summary>Everything on the CPU (C# with SIMD on all cores).</summary>
    Cpu,
    /// <summary>The heavy pixel routines on the GPU through Vulkan; the CPU where there is no Vulkan.</summary>
    Gpu,
}

/// <summary>How often the interpreter runs a frame.</summary>
public enum FramePacing
{
    /// <summary>The game's own pace, as the engine keeps it: up to 60 frames a second, longer when the scripts sleep (002A); the window draws only new pictures.</summary>
    Game,
    /// <summary>A frame for every refresh of the screen (smoother on a fast screen; a 240 Hz one runs the scripts four times as often).</summary>
    Display,
}

public sealed class PlayerSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<Renderer>))]
    public Renderer Renderer { get; set; } = Renderer.Cpu;

    [JsonConverter(typeof(JsonStringEnumConverter<FramePacing>))]
    public FramePacing FramePacing { get; set; } = FramePacing.Game;

    /// <summary>The scripts see the first joystick or gamepad (the games' own RIO.INI turns it off).</summary>
    public bool GameController { get; set; } = true;

    /// <summary>Frames a second and the slowest frame: in the window's title, on phones over the game.</summary>
    public bool ShowFrameRate { get; set; } = true;

    /// <summary>perf.log in the save folder: every second where the time went (halves the interpreter's speed).</summary>
    public bool PerfLog { get; set; }

    /// <summary>Embedded x86 code without a C# version is translated to .NET (off: interpreted, slow; for bug reports).</summary>
    public bool TranslateX86 { get; set; } = true;

    /// <summary>draw-trace.log in the save folder: what every frame drew (for bug reports on flicker).</summary>
    public bool DrawTrace { get; set; }

    private static string SettingsFile => Path.Combine(PlayerFolders.Root, "settings.json");
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>The saved settings, or the defaults when there are none (or they cannot be read).</summary>
    public static PlayerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile) && JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(SettingsFile), s_json) is { } settings)
                return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The defaults
        }
        return new PlayerSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(PlayerFolders.Root);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, s_json));
    }

    /// <summary>
    /// Makes the GPU accelerator (set by each player that carries OpenShiina.Gpu); null when the
    /// player has none.
    /// </summary>
    public static Func<(IScnAccelerator? Accelerator, string? Error)>? AcceleratorFactory { get; set; }

    private static (IScnAccelerator? Accelerator, string? Error)? s_accelerator;
    private static readonly Lock s_lock = new();

    /// <summary>
    /// The GPU, made the first time it is asked for and kept for the rest of the run (games are
    /// played one at a time); null with the reason when there is none.
    /// </summary>
    public static IScnAccelerator? Gpu(out string? error)
    {
        lock (s_lock)
        {
            s_accelerator ??= AcceleratorFactory?.Invoke() ?? (null, "This player has no GPU mode.");
            error = s_accelerator.Value.Error;
            return s_accelerator.Value.Accelerator;
        }
    }

    /// <summary>
    /// The accelerator a game starts with: the GPU when the settings ask for it (OPENSHIINA_GPU=1
    /// or 0 overrides them) and there is one; null for the CPU.
    /// </summary>
    public IScnAccelerator? AcceleratorForGame()
    {
        bool gpu = Environment.GetEnvironmentVariable("OPENSHIINA_GPU") switch
        {
            "1" => true,
            "0" => false,
            _ => Renderer == Renderer.Gpu,
        };
        return gpu ? Gpu(out _) : null;
    }

    /// <summary>
    /// ScnVm.Joypad for a game: the setting, or OPENSHIINA_JOYPAD (0 off, 1 on, ini: RIO.INI's
    /// Joypad= as the engine reads it, null here).
    /// </summary>
    public bool? JoypadForGame() => Environment.GetEnvironmentVariable("OPENSHIINA_JOYPAD") switch
    {
        "0" => false,
        "ini" => null,
        { Length: > 0 } => true,
        _ => GameController,
    };

    /// <summary>The frame rate meter: in the title, and perf.log; OPENSHIINA_PERF=0 none, =log both, else the title.</summary>
    public (bool Title, bool Log) PerfForGame() => Environment.GetEnvironmentVariable("OPENSHIINA_PERF") switch
    {
        "0" => (false, false),
        "log" => (true, true),
        { Length: > 0 } => (true, false),
        _ => (ShowFrameRate, PerfLog),
    };

    /// <summary>Embedded x86 code translated (ScnVm.JitX86); OPENSHIINA_X86JIT=0 turns it off.</summary>
    public bool X86JitForGame() => Environment.GetEnvironmentVariable("OPENSHIINA_X86JIT") != "0" && TranslateX86;

    /// <summary>draw-trace.log; OPENSHIINA_TRACE=draw turns it on.</summary>
    public bool DrawTraceForGame() => Environment.GetEnvironmentVariable("OPENSHIINA_TRACE") == "draw" || DrawTrace;

    /// <summary>The on / off settings as the settings screens show them, in sections.</summary>
    public static IReadOnlyList<SettingSwitch> Switches { get; } =
    [
        new("Game", "Game controller",
            "The first joystick or gamepad plays: the stick moves, button 1 decides, button 2 cancels.",
            s => s.GameController, (s, on) => s.GameController = on),
        new("Performance", "Show the frame rate",
            "Frames a second and the slowest frame, in the window's title (on phones and tablets over the game).",
            s => s.ShowFrameRate, (s, on) => s.ShowFrameRate = on),
        new("Performance", "Write perf.log",
            "Every second, where the time went, into the game's save folder. The game runs slower while it writes.",
            s => s.PerfLog, (s, on) => s.PerfLog = on),
        new("Troubleshooting", "Translate the games' x86 code",
            "Off runs it on the interpreter instead: much slower; only to tell whether a problem comes from the translation.",
            s => s.TranslateX86, (s, on) => s.TranslateX86 = on),
        new("Troubleshooting", "Write draw-trace.log",
            "What every frame draws, into the game's save folder when the game ends (for reports on flicker).",
            s => s.DrawTrace, (s, on) => s.DrawTrace = on),
    ];
}

/// <summary>An on / off setting: its section and name on the settings screens, a line about it, and its value.</summary>
public sealed record SettingSwitch(string Section, string Label, string Help, Func<PlayerSettings, bool> Get, Action<PlayerSettings, bool> Set);
