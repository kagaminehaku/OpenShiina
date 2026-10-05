// The player's OPTION settings and the messages already read, kept per game in
// %AppData%\OpenShiina\config (see PlayerFolders). Defaults are the engine's (START.SCN 23989):
// sliders b[1] / b[2] / b[5] = 19 / 39 / 26 of 49, message speed b[3] = 2 (標準).

using System.IO;
using System.Text.Json;

namespace OpenShiina.Story;

public sealed class PlayerConfig
{
    public double MusicVolume { get; set; } = 19 / 49.0;
    public double VoiceVolume { get; set; } = 39 / 49.0;
    public double EffectVolume { get; set; } = 26 / 49.0;
    public bool MusicOn { get; set; } = true;
    public bool VoiceOn { get; set; } = true;
    public bool EffectOn { get; set; } = true;
    public bool FullScreen { get; set; }

    /// <summary>0 高速, 1 速い, 2 標準, 3 遅い: "_w" = value * 18 ms per character.</summary>
    public int MessageSpeed { get; set; } = 2;

    /// <summary>0 高速 - 3 遅い: how long auto mode waits after a message.</summary>
    public int AutoWait { get; set; } = 2;

    /// <summary>メッセージスキップ: false = 既読 (skip only messages read before), true = 未読 (all).</summary>
    public bool SkipUnread { get; set; }

    public int CharacterMs => MessageSpeed * 18;

    /// <summary>Wait in auto mode after the voice ended, or after the text when there is none.</summary>
    public int AutoDelay(int length, bool voiced) =>
        new[] { 300, 700, 1200, 2000 }[Math.Clamp(AutoWait, 0, 3)] + (voiced ? 0 : 50 * length);

    private static string Folder(string game) => PlayerFolders.For("config", game);

    public static PlayerConfig Load(string game)
    {
        try
        {
            string path = Path.Combine(Folder(game), "config.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PlayerConfig>(File.ReadAllText(path)) ?? new PlayerConfig();
        }
        catch
        {
            // A damaged file gives the defaults
        }
        return new PlayerConfig();
    }

    public void Save(string game)
    {
        try
        {
            Directory.CreateDirectory(Folder(game));
            File.WriteAllText(Path.Combine(Folder(game), "config.json"), JsonSerializer.Serialize(this));
        }
        catch
        {
            // Settings that cannot be written apply for this session only
        }
    }

    /// <summary>Messages read before ("FILE:index"), for skipping read text only.</summary>
    public static HashSet<string> LoadRead(string game)
    {
        try
        {
            string path = Path.Combine(Folder(game), "read.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
        }
        return new HashSet<string>();
    }

    public static void SaveRead(string game, HashSet<string> read)
    {
        try
        {
            Directory.CreateDirectory(Folder(game));
            File.WriteAllText(Path.Combine(Folder(game), "read.json"), JsonSerializer.Serialize(read));
        }
        catch
        {
        }
    }
}
