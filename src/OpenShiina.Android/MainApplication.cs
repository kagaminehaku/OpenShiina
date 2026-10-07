// The Android application: before Avalonia starts, the schemes (Formats.Json, ShiinaImage) are
// copied out of the package to the app's folder, where FormatManager reads them; the player's own
// folder (library.json, saves, logs) is the app's folder on the shared storage
// (Android/data/com.kagaminehaku.openshiina/files), which a computer can reach over USB; and the
// player is told what is Android's (PlayerPlatform).

using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using OpenShiina.App;
using OpenShiina.Archives;

namespace OpenShiina.Android;

[global::Android.App.Application]
public sealed class MainApplication : AvaloniaAndroidApplication<OpenShiinaApplication>
{
    public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        try
        {
            FormatManager.DataFolder = CopyData();
        }
        catch (Exception e)
        {
            // FormatManager says it found no schemes; the reason goes to the log
            global::Android.Util.Log.Error("OpenShiina", $"Could not copy the schemes: {e}");
        }
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("OPENSHIINA_DATA")))
        {
            string root = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            System.Environment.SetEnvironmentVariable("OPENSHIINA_DATA", root);
        }
        PlayerPlatform.Touch = true;
        PlayerPlatform.StartSound = AndroidSoundOutput.TryStart;
        PlayerPlatform.OpenJoystick = () => null;
        PlayerPlatform.FolderPath = AndroidStorage.FolderPath;
        PlayerPlatform.EnsureFileAccessAsync = AndroidStorage.EnsureFileAccessAsync;
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).LogToTrace();

    /// <summary>The package's Formats.Json and ShiinaImage files in a folder of the app: its path.</summary>
    private string CopyData()
    {
        var assets = Assets ?? throw new InvalidOperationException("The package has no assets.");
        // Copied at each start (half a megabyte), so a new version of the app brings its own
        string target = Path.Combine(FilesDir!.AbsolutePath, "schemes");
        Copy(FormatManager.SchemeFileName);
        foreach (string name in assets.List("ShiinaImage") ?? [])
            Copy($"ShiinaImage/{name}");
        return target;

        void Copy(string name)
        {
            string path = Path.Combine(target, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = assets.Open(name);
            using var file = File.Create(path);
            source.CopyTo(file);
        }
    }
}
