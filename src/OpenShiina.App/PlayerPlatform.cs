// What differs between the platforms the player runs on, set by each head before the view is
// made: the desktop (OpenShiina.Desktop) keeps the defaults - SDL3 for sound and joysticks, folders
// as the picker gives them - and Android (OpenShiina.Android) plays the sound with AudioTrack, asks
// for access to the files, turns the picker's document URIs into paths and wants the touch controls.

using Avalonia.Platform.Storage;
using NAudio.Wave;
using OpenShiina.Scripting;

namespace OpenShiina.App;

/// <summary>A game controller read each frame the view draws.</summary>
public interface IPlayerJoystick : IDisposable
{
    /// <summary>The joystick now, or null when there is none.</summary>
    ScnJoystick? Poll();
}

public static class PlayerPlatform
{
    /// <summary>
    /// Plays the engine's mix (44.1 kHz stereo float) on the sound device until the result is
    /// disposed; null when there is no sound device (the game runs silent).
    /// </summary>
    public static Func<ISampleProvider, IDisposable?> StartSound { get; set; } = source => SdlAudioOutput.TryStart(source);

    /// <summary>Joystick 0, or null when the platform reads none.</summary>
    public static Func<IPlayerJoystick?> OpenJoystick { get; set; } = () => new SdlJoystick();

    /// <summary>The screen is the only input (phones, tablets): the touch controls show from the start.</summary>
    public static bool Touch { get; set; }

    /// <summary>The path of a folder the folder picker gave, or null when it has none.</summary>
    public static Func<IStorageFolder, string?> FolderPath { get; set; } = folder => folder.TryGetLocalPath();

    /// <summary>
    /// Makes sure the player may read the games' folders (Android asks the user); false when it may
    /// not yet - the player then says so and the user tries again.
    /// </summary>
    public static Func<Task<bool>> EnsureFileAccessAsync { get; set; } = () => Task.FromResult(true);

    /// <summary>The app went to the background (false) or came back (true), where the platform has no window to say so.</summary>
    public static event Action<bool>? ActiveChanged;

    public static void SetActive(bool active) => ActiveChanged?.Invoke(active);
}
