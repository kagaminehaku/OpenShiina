// The players' settings: settings.json in the player folder (PlayerFolders.Root), shared by both
// players. For now one: whether the interpreter's pixel work runs on the CPU or on the GPU
// (Vulkan, OpenShiina.Gpu); a change counts from the next game started.

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

public sealed class PlayerSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<Renderer>))]
    public Renderer Renderer { get; set; } = Renderer.Cpu;

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
}
