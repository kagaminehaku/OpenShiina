// New releases from GitHub: the latest release of OpenShiina's repository (its API's
// releases/latest), newer than this build when its tag is, and its package for this platform: the
// Windows zip, the Linux tar.gz or the Android APK. Only builds of a release look (AppVersion.IsRelease:
// not "-dev" ones), when the setting CheckGitHub is on, and a release the user skipped is not shown
// again.
//
// Installing it: on Android the system's installer takes the APK (PlayerPlatform.InstallPackage).
// On Windows and Linux the program is one file (PublishSingleFile) with Formats.Json and the
// licences beside it: the package is unpacked into the player folder's update\, the running
// program renamed to <name>.old (both systems allow renaming a program that runs), the new files
// put in its folder and the new program started; the .old is deleted at the next start. A folder
// the player may not write in (Program Files) leaves everything as it was: the release's page opens
// instead.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using OpenShiina.Game;

namespace OpenShiina.App;

/// <summary>A newer release: its version, what changed, its page, and its package for this platform.</summary>
public sealed record ReleaseInfo(string Version, string Notes, string Page, string PackageUrl, string PackageName, long PackageSize);

public static class AppUpdater
{
    /// <summary>The newer release, or null (none, none for this platform, offline, or not a release's build).</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancel = default)
    {
        if (!AppVersion.IsRelease)
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{SharedTranslations.Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await SharedTranslations.Http.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
                return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var root = json.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!AppVersion.IsNewer(tag))
                return null;
            string suffix = PackageSuffix();
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("OpenShiina-", StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal))
                    return new ReleaseInfo(tag.TrimStart('v'), root.GetProperty("body").GetString() ?? "", root.GetProperty("html_url").GetString() ?? "",
                        asset.GetProperty("browser_download_url").GetString() ?? "", name, asset.GetProperty("size").GetInt64());
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }
        return null;
    }

    /// <summary>The end of this platform's package's name.</summary>
    private static string PackageSuffix() =>
        OperatingSystem.IsAndroid() ? "-android.apk"
        : OperatingSystem.IsWindows() ? "-windows-x64.zip"
        : "-linux-x64.tar.gz";

    /// <summary>
    /// Downloads the release's package and installs it: true when the new program was started
    /// (the caller then quits) or the system's installer took it; false when it cannot be done
    /// here (the caller opens the release's page). <paramref name="progress"/> gets the bytes so far.
    /// </summary>
    public static async Task<bool> InstallAsync(ReleaseInfo release, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        string folder = Path.Combine(PlayerFolders.Root, "update");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        string package = Path.Combine(folder, release.PackageName);
        using (var response = await SharedTranslations.Http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            await using var from = await response.Content.ReadAsStreamAsync(cancel);
            await using var to = File.Create(package);
            var buffer = new byte[1 << 16];
            long done = 0;
            for (int n; (n = await from.ReadAsync(buffer, cancel)) > 0; )
            {
                await to.WriteAsync(buffer.AsMemory(0, n), cancel);
                done += n;
                progress?.Report(done);
            }
        }
        if (new FileInfo(package).Length != release.PackageSize)
            throw new InvalidDataException($"{release.PackageName} did not come whole");
        if (PlayerPlatform.InstallPackage is { } install)
            return install(package);
        return await Task.Run(() => ReplaceProgram(package, Path.Combine(folder, "files")), cancel);
    }

    /// <summary>The desktop: the package's files in place of the running program's, the new program started.</summary>
    private static bool ReplaceProgram(string package, string unpacked)
    {
        string? program = Environment.ProcessPath;
        if (program == null)
            return false;
        string here = Path.GetDirectoryName(program)!, name = Path.GetFileName(program);
        if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(package, unpacked, overwriteFiles: true);
        else
        {
            using var gz = new GZipStream(File.OpenRead(package), CompressionMode.Decompress);
            Directory.CreateDirectory(unpacked);
            TarFile.ExtractToDirectory(gz, unpacked, overwriteFiles: true);
        }
        // (the tar.gz holds a folder, OpenShiina/, the zip the files themselves)
        string? from = Directory.EnumerateFiles(unpacked, name, SearchOption.AllDirectories).FirstOrDefault();
        if (from == null)
            return false;
        string source = Path.GetDirectoryName(from)!;
        string old = program + ".old";
        try
        {
            if (File.Exists(old))
                File.Delete(old);
            File.Move(program, old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        try
        {
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(here, Path.GetFileName(file)), overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(program, File.GetUnixFileMode(old));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Put the running one back
            if (!File.Exists(program))
                File.Move(old, program);
            return false;
        }
        Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = here });
        return true;
    }

    /// <summary>At the start: the program an update replaced is deleted, and the downloaded package.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Environment.ProcessPath is { } program && File.Exists(program + ".old"))
                File.Delete(program + ".old");
            string folder = Path.Combine(PlayerFolders.Root, "update");
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // (the old program can still be running for a moment: next time)
        }
    }
}
