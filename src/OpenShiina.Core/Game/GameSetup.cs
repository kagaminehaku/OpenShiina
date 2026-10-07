// What every front end does to start a game: read RIO.INI (the engine version, the first script,
// the window size), make the interpreter for that version with the platform's host, load the
// first script into slot 0 as the engine does, and keep save data in the player's own folder.

using System.IO;
using System.Text.RegularExpressions;

namespace OpenShiina.Game;

public sealed class GameSetup
{
    public string Version { get; }
    public string StartScript { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Where this game's save data goes: %AppData%\OpenShiina\scn\&lt;game&gt; (made when missing).</summary>
    public string SaveFolder { get; }

    private GameSetup(string version, string start, int width, int height, string saves)
    {
        Version = version;
        StartScript = start;
        Width = width;
        Height = height;
        SaveFolder = saves;
    }

    /// <summary>Reads the game's RIO.INI.</summary>
    public static GameSetup Read(GameData data)
    {
        string ini = Encodings.cp932.GetString(data.LooseFile("RIO.INI") is { } path ? File.ReadAllBytes(path) : []);
        string version = Regex.Match(ini, @"v(\d+\.\d+)").Groups[1].Value;
        if (version.Length == 0)
            throw new InvalidDataException("RIO.INI is missing or names no engine version.");
        string start = Regex.Match(ini, @"(?im)^Scn=(.+)$").Groups[1].Value.Trim();
        if (start.Length == 0)
            start = "autoexec.scn";     // the engine's own default
        string saves = PlayerFolders.For("scn", data.SchemeName);
        Directory.CreateDirectory(saves);
        return new GameSetup(version, start, Number(ini, "WindowWidth", 800), Number(ini, "WindowHeight", 600), saves);
    }

    private static int Number(string ini, string key, int fallback) =>
        Regex.Match(ini, $@"(?im)^{key}=(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : fallback;

    /// <summary>
    /// The interpreter for this engine version with the first script loaded and started.
    /// OPENSHIINA_X86JIT=0 leaves embedded x86 routines without a C# version to the interpreter.
    /// </summary>
    public ScnVm CreateVm(IScnHost host)
    {
        var vm = new ScnVm(ScnOpcodes.ForVersion(Version), host)
        {
            EngineVersion = (int)Math.Round(double.Parse(Version, System.Globalization.CultureInfo.InvariantCulture) * 100),
            ScreenWidth = Width,
            ScreenHeight = Height,
        };
        if (Environment.GetEnvironmentVariable("OPENSHIINA_X86JIT") == "0")
            vm.JitX86 = false;
        if (!vm.LoadModule(0, StartScript, start: true))
            throw new InvalidDataException($"{StartScript} is missing.");
        return vm;
    }
}
