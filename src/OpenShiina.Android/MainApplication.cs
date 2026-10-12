// The Android application: before Avalonia starts, the schemes (Formats.Json) are
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
        {
            string root = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
            System.Environment.SetEnvironmentVariable("OPENSHIINA_DATA", root);
        }
        PlayerPlatform.Touch = true;
        PlayerPlatform.StartSound = AndroidSoundOutput.TryStart;
        PlayerPlatform.OpenJoystick = () => null;
        PlayerPlatform.FolderPath = AndroidStorage.FolderPath;
        PlayerPlatform.EnsureFileAccessAsync = AndroidStorage.EnsureFileAccessAsync;
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
