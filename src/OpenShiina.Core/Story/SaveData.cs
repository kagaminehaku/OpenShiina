// Save slots of the story player. A save records where the story is (routes played, scenario
// file, message number); loading replays that file silently up to the message, which rebuilds
// the pictures, music and loops on screen at that point.
// Slots are numbered like the game's: ten pages of ten, the last page (AUTO) holding the auto
// saves AUTO1-AUTO9 (90-98, newest first) and the quick save (99).

using System.IO;
using System.Text.Json;

namespace OpenShiina.Story;

/// <summary>
/// A save: the scenario file and message, the routes played, the message text and time; with a flow
/// script (SRC_MAIN.SCN) its global variables, which bring it back to the file as the game does.
/// </summary>
public sealed record SaveData(string File, int Message, int Played, string Text, DateTime Time)
{
    public Dictionary<string, int>? Vars { get; init; }

    /// <summary>Script line of the message (-1 in older saves, which count messages instead).</summary>
    public int Line { get; init; } = -1;
}

/// <summary>Where the player keeps saves and settings: %AppData%\OpenShiina, or OPENSHIINA_DATA.</summary>
public static class PlayerFolders
{
    public static string Root =>
        Environment.GetEnvironmentVariable("OPENSHIINA_DATA") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenShiina");

    public static string For(string kind, string game) =>
        Path.Combine(Root, kind, string.Concat(game.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
}

public sealed class SaveStore
{
    public const int SlotsPerPage = 10, Pages = 10;
    public const int AutoPage = 9, FirstAuto = 90, AutoSlots = 9, QuickSlot = 99;

    private readonly string m_folder;

    public SaveStore(string gameName)
    {
        m_folder = PlayerFolders.For("saves", gameName);
    }

    private string DataPath(int slot) => Path.Combine(m_folder, $"save{slot:D3}.json");
    private string ThumbnailPath(int slot) => Path.Combine(m_folder, $"save{slot:D3}.png");

    public SaveData? Read(int slot)
    {
        try
        {
            return File.Exists(DataPath(slot)) ? JsonSerializer.Deserialize<SaveData>(File.ReadAllText(DataPath(slot))) : null;
        }
        catch
        {
            return null;    // A damaged save shows as empty
        }
    }

    /// <summary>The save's thumbnail as PNG bytes, or null.</summary>
    public byte[]? Thumbnail(int slot)
    {
        try
        {
            return File.Exists(ThumbnailPath(slot)) ? File.ReadAllBytes(ThumbnailPath(slot)) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Write(int slot, SaveData data, PixelImage thumbnail)
    {
        Directory.CreateDirectory(m_folder);
        File.WriteAllText(DataPath(slot), JsonSerializer.Serialize(data));
        File.WriteAllBytes(ThumbnailPath(slot), PngEncoder.Encode(thumbnail));
    }

    /// <summary>
    /// An auto save (START.SCN function 197): AUTO1-AUTO8 move down one slot, the oldest
    /// (AUTO9) is dropped, and the new save becomes AUTO1.
    /// </summary>
    public void WriteAuto(SaveData data, PixelImage thumbnail)
    {
        Directory.CreateDirectory(m_folder);
        for (int slot = FirstAuto + AutoSlots - 2; slot >= FirstAuto; slot--)
        {
            foreach (var (from, to) in new[] { (DataPath(slot), DataPath(slot + 1)), (ThumbnailPath(slot), ThumbnailPath(slot + 1)) })
            {
                if (File.Exists(from))
                    File.Move(from, to, overwrite: true);
                else if (File.Exists(to))
                    File.Delete(to);    // An empty slot moves down too
            }
        }
        Write(FirstAuto, data, thumbnail);
    }

    /// <summary>The message stored with a save: its first 22 bytes (11 full-width characters), then "..." (START.SCN 2C7EB).</summary>
    public static string Caption(string text)
    {
        string shown = ScenarioScript.DisplayText(text);
        int bytes = 0;
        for (int i = 0; i < shown.Length; i++)
        {
            bytes += MessageLayout.IsHalfWidth(shown[i]) ? 1 : 2;
            if (bytes > 22)
                return shown[..i] + "...";
        }
        return shown;
    }
}
