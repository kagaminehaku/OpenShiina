// The Android application: before Avalonia starts, the schemes (Formats.Json) are
// copied out of the package to the app's folder, where FormatManager reads them; the player's own
// folder (library.json, saves, logs) is OpenShiina on the device's storage once the player may
// write there, else the app's folder on it (Android/data/com.kagaminehaku.openshiina/files;
// PlayerRoot); and the player is told what is Android's (PlayerPlatform).

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
        // startup.log: the time before this, from the process's start, is the runtime's own
        Game.StartupLog.Begin(global::Android.OS.SystemClock.ElapsedRealtime() - global::Android.OS.Process.StartElapsedRealtime,
            "the app's code starts (MainApplication.OnCreate)");
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
            System.Environment.SetEnvironmentVariable("OPENSHIINA_DATA", PlayerRoot());
        PlayerPlatform.Touch = true;
        PlayerPlatform.StartSound = AndroidSoundOutput.TryStart;
        PlayerPlatform.OpenJoystick = () => null;
        PlayerPlatform.FolderPath = AndroidStorage.FolderPath;
        PlayerPlatform.EnsureFileAccessAsync = AndroidStorage.EnsureFileAccessAsync;
        PlayerPlatform.OpenFolder = AndroidStorage.OpenFolder;
        Game.StartupLog.Mark("schemes copied");
        // CoreCLR on .NET 10 loads a library the app does not ship (libandroid.so here) by asking
        // the main thread, on the first P/Invoke into it from another thread, giving up after 3 s
        // for each of its two names. Avalonia's frame timer makes its first call into libandroid.so
        // on its own thread while the main thread waits for it (the compositor's first frame):
        // 6 s more to open the app (logcat: "Timeout while waiting for shared library
        // 'libandroid.so' to load"; dotnet/android#12826, fixed for .NET 11 by #12863). Called here
        // on the main thread first, it is loaded in under 2 ms and the timer finds it.
        _ = AChoreographer_getInstance();
        Game.StartupLog.Mark("libandroid.so loaded, Avalonia starts");
        base.OnCreate();
        Game.StartupLog.Mark("Avalonia's application made");
    }

    [System.Runtime.InteropServices.DllImport("android")]
    private static extern IntPtr AChoreographer_getInstance();

    // startup.log: Avalonia's own steps, each of its platform services, and its log (everything it
    // says while it starts, at the debug level, with the time)
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        Game.StartupLog.Mark("Avalonia: the builder is made");
        System.Diagnostics.Trace.Listeners.Add(new Game.StartupLog.TraceListener());
        builder = base.CustomizeAppBuilder(builder);
        // Each of the platform services timed (Avalonia sets them up in this order)
        builder = builder.UseRuntimePlatformSubsystem(Timed(builder.RuntimePlatformServicesInitializer, "runtime platform", builder.RuntimePlatformServicesName),
            builder.RuntimePlatformServicesName ?? "");
        builder = builder.UseTextShapingSubsystem(Timed(builder.TextShapingSubsystemInitializer, "text shaping", builder.TextShapingSubsystemName),
            builder.TextShapingSubsystemName ?? "");
        builder = builder.UseRenderingSubsystem(Timed(builder.RenderingSubsystemInitializer, "rendering", builder.RenderingSubsystemName),
            builder.RenderingSubsystemName ?? "");
        builder = builder.UseWindowingSubsystem(Timed(builder.WindowingSubsystemInitializer, "windowing", builder.WindowingSubsystemName),
            builder.WindowingSubsystemName ?? "");
        return builder.LogToTrace(Avalonia.Logging.LogEventLevel.Debug)
            .AfterPlatformServicesSetup(_ => Game.StartupLog.Mark("Avalonia: platform services set up (windowing, rendering)"))
            .AfterSetup(_ => Game.StartupLog.Mark("Avalonia: set up"));

        static Action Timed(Action? initializer, string what, string? name)
        {
            return () =>
            {
                initializer?.Invoke();
                Game.StartupLog.Mark($"  Avalonia: {what} ({name ?? "none"})" + (initializer == null ? " not given" : ""));
            };
        }
    }

    /// <summary>
    /// The player folder: OpenShiina on the device's storage when the player may write there (all
    /// files access, which it asks for to read the games), where any file manager and a computer
    /// over USB see the saves; else the app's own folder there (Android/data/.../files, which file
    /// managers do not show from Android 11). Once, the files of the app's folder that the shared
    /// one does not have yet are copied there: the saves of an earlier version, a backup Android
    /// put back on a new device (BackupAgent works in the app's folder).
    /// </summary>
    private string PlayerRoot()
    {
        string own = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        if (!AndroidStorage.HasSharedAccess(this))
            return own;
        string shared = AndroidStorage.SharedPlayerFolder;
        // Once: a save deleted from the shared folder is not brought back. The mark is a .log, which
        // the backup leaves out, so a backup put back on a new device is copied again
        string moved = Path.Combine(own, "moved-to-shared.log");
        try
        {
            Directory.CreateDirectory(shared);
            if (!File.Exists(moved))
            {
                CopyMissing(own, shared);
                File.WriteAllText(moved, $"The player folder is {shared} since {DateTime.Now:yyyy-MM-dd HH:mm}.\n");
            }
            return shared;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            global::Android.Util.Log.Error("OpenShiina", $"The player folder stays {own}: {e.Message}");
            return own;
        }

        static void CopyMissing(string from, string to)
        {
            if (!Directory.Exists(from))
                return;
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                if (File.Exists(target))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            }
        }
    }

    /// <summary>The package's Formats.Json in a folder of the app: its path.</summary>
    private string CopyData()
    {
        var assets = Assets ?? throw new InvalidOperationException("The package has no assets.");
        // Copied at each start (under two megabytes), so a new version of the app brings its own
        string target = Path.Combine(FilesDir!.AbsolutePath, "schemes");
        Copy(FormatManager.SchemeFileName);
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
