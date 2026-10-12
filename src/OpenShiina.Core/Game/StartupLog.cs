// startup.log in the player folder: how long each step of opening the app took, counted from the
// process's start, written once the first frame of the library is on screen. The steps tell the
// runtime's own start (before any of the app's code runs) from the app's; they found the 6 s a
// phone waited on CoreCLR in Avalonia's frame timer (MainApplication.OnCreate, 2026-10-12).

using System.Diagnostics;
using System.IO;

namespace OpenShiina.Game;

public static class StartupLog
{
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static readonly List<string> s_lines = new();
    private static readonly Lock s_lock = new();
    private static double s_before = -1;
    private static bool s_written;

    /// <summary>
    /// The first mark: <paramref name="beforeMs"/> milliseconds went by between the process's start
    /// and this call (the runtime starting); null to take it from the process's start time.
    /// </summary>
    public static void Begin(double? beforeMs, string what)
    {
        lock (s_lock)
        {
            if (s_before >= 0)
                return;
            s_before = beforeMs ?? SinceProcessStart();
            s_clock.Restart();
            s_lines.Add(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
                + (Type.GetType("Mono.RuntimeStructs") != null ? " (Mono)" : "")
                + $", {Environment.ProcessorCount} processors");
            s_lines.Add($"{s_before,8:F0} ms  {what}");
        }
    }

    /// <summary>A step done now.</summary>
    public static void Mark(string what)
    {
        lock (s_lock)
        {
            if (s_written)
                return;
            if (s_before < 0)
                Begin(null, "(the app's code starts)");
            s_lines.Add($"{s_before + s_clock.Elapsed.TotalMilliseconds,8:F0} ms  {what}");
        }
    }

    /// <summary>The last step: the log is written (once).</summary>
    public static void Finish(string what)
    {
        Mark(what);
        lock (s_lock)
        {
            if (s_written)
                return;
            s_written = true;
            try
            {
                Directory.CreateDirectory(PlayerFolders.Root);
                File.WriteAllLines(Path.Combine(PlayerFolders.Root, "startup.log"), s_lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only a measurement
            }
        }
    }

    /// <summary>
    /// Lines written to System.Diagnostics.Trace (Avalonia's log with LogToTrace), each a mark,
    /// until the log is written (200 at most).
    /// </summary>
    public sealed class TraceListener : System.Diagnostics.TraceListener
    {
        private int m_lines;
        private string m_partial = "";

        public override void Write(string? message)
        {
            lock (this)
                m_partial += message;
        }

        public override void WriteLine(string? message)
        {
            string line;
            lock (this)
            {
                line = m_partial + message;
                m_partial = "";
                if (s_written || ++m_lines > 200 || line.Length == 0)
                    return;
            }
            Mark("  log: " + (line.Length > 200 ? line[..200] : line));
        }
    }

    private static double SinceProcessStart()
    {
        try
        {
            return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}
