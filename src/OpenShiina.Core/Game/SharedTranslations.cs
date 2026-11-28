// Translations shared on GitHub. The folder translations/ of OpenShiina's repository holds a folder
// for each game (any name) with a folder for each of its translations: info.json (which game, as
// its scheme in Formats.Json is named; the language; a title; who made it) and the files of a
// translation folder (text.tsv, names.tsv, strings.tsv...). The catalog is that folder's tree, read
// in one request of GitHub's API (git/trees/master:translations?recursive=1, asked again with its
// ETag: an unchanged tree does not count against the API's hourly allowance), every file with its
// git blob SHA-1 and size; info.json and the translations' files come from raw.githubusercontent.com
// and are checked against those SHA-1s, so a file cut short or changed on the way is not kept.
//
// A translation downloaded is kept in the player's folder (translations\<game>\<translation>, with
// installed.json: what it is and the SHA-1 of each file), never in the game's folder: the player
// reads it in place of the game folder's translation\ when it is the one chosen for the game
// (GameData.TranslationFolder). Updating one downloads only the files whose SHA-1 changed, into
// ".part" files first, and puts them in place once all have come. What was chosen for each game and
// the shared translations it has been shown are in translations\choices.json.

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenShiina.Game;

/// <summary>A file of a shared translation: its name in the translation's folder, its git blob SHA-1 and size.</summary>
public sealed record SharedFile(string Name, string Sha, long Size);

/// <summary>A translation of the catalog (or one downloaded, when the catalog cannot be read).</summary>
public sealed class SharedTranslation
{
    /// <summary>Its folder under translations/ ("Raillore no Ryakudatsusha/vi"): who it is.</summary>
    public string Path { get; set; } = "";
    /// <summary>The game it is for: its scheme's name.</summary>
    public string Game { get; set; } = "";
    /// <summary>The language, as a code ("vi", "en").</summary>
    public string Language { get; set; } = "";
    public string Title { get; set; } = "";
    public string[] Authors { get; set; } = [];
    /// <summary>"human", "machine" or "machine+edited".</summary>
    public string Kind { get; set; } = "";
    /// <summary>How much of the game it translates, in percent (0 when not said).</summary>
    public int Progress { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>The day of its last change as its makers give it ("2026-11-28").</summary>
    public string Updated { get; set; } = "";
    public List<SharedFile> Files { get; set; } = [];

    [JsonIgnore]
    public long Size => Files.Sum(f => f.Size);
}

/// <summary>The shared translations as GitHub has them.</summary>
public sealed class SharedCatalog
{
    public List<SharedTranslation> Translations { get; } = [];

    /// <summary>The translations of a game (by its scheme's name).</summary>
    public List<SharedTranslation> For(string game) =>
        Translations.Where(t => string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>What was chosen for a game: <see cref="Original"/>, <see cref="Local"/> or a shared translation's path; empty: not asked yet.</summary>
public sealed class TranslationChoice
{
    public const string Original = "original", Local = "local";

    public string Choice { get; set; } = "";
    /// <summary>The shared translations the game has been shown (a new one gets a mark, not a question).</summary>
    public List<string> Seen { get; set; } = [];
}

public static class SharedTranslations
{
    public const string Repository = "kagaminehaku/OpenShiina", Branch = "master", Folder = "translations";
    private const string InfoFile = "info.json", InstalledFile = "installed.json";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lazy<HttpClient> s_http = new(() =>
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenShiina", AppVersion.Text));
        return http;
    });

    /// <summary>The HTTP client of the player's requests to GitHub (its translations and releases).</summary>
    public static HttpClient Http => s_http.Value;

    /// <summary>Where the downloaded translations, the catalog's copy and the choices are.</summary>
    public static string Root => System.IO.Path.Combine(PlayerFolders.Root, "translations");

    // ---- The catalog

    private sealed class TreeCache
    {
        public string ETag { get; set; } = "";
        public List<SharedFile> Files { get; set; } = [];
    }

    /// <summary>
    /// The catalog as GitHub has it now, or null when it cannot be read (offline, GitHub's
    /// allowance spent): the translations' info.json read too (kept by their SHA-1).
    /// </summary>
    public static async Task<SharedCatalog?> FetchAsync(CancellationToken cancel = default)
    {
        string cacheFile = System.IO.Path.Combine(Root, "catalog.json");
        TreeCache? cache = null;
        try
        {
            if (File.Exists(cacheFile))
                cache = JsonSerializer.Deserialize<TreeCache>(await File.ReadAllTextAsync(cacheFile, cancel), s_json);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        List<SharedFile> files;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/git/trees/{Branch}:{Folder}?recursive=1");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (cache is { ETag.Length: > 0 })
                request.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);
            using var response = await Http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.NotModified && cache != null)
                files = cache.Files;
            else if (response.StatusCode == HttpStatusCode.NotFound)
                files = [];
            else
            {
                response.EnsureSuccessStatusCode();
                using var tree = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
                files = tree.RootElement.GetProperty("tree").EnumerateArray()
                    .Where(e => e.GetProperty("type").GetString() == "blob")
                    .Select(e => new SharedFile(e.GetProperty("path").GetString() ?? "", e.GetProperty("sha").GetString() ?? "",
                        e.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
                    .ToList();
                Directory.CreateDirectory(Root);
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(
                    new TreeCache { ETag = response.Headers.ETag?.ToString() ?? "", Files = files }, s_json), cancel);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }

        // A translation: a folder two deep (game, translation) with an info.json
        var catalog = new SharedCatalog();
        foreach (var group in files.Where(f => f.Name.Count(c => c == '/') >= 2)
                     .GroupBy(f => string.Join('/', f.Name.Split('/').Take(2)), StringComparer.Ordinal))
        {
            var info = group.FirstOrDefault(f => f.Name == group.Key + "/" + InfoFile);
            if (info == null || await ReadInfoAsync(group.Key, info, cancel) is not { } translation)
                continue;
            translation.Path = group.Key;
            translation.Files = group.Where(f => f != info && f.Name.Count(c => c == '/') == 2)
                .Select(f => f with { Name = f.Name[(group.Key.Length + 1)..] }).ToList();
            if (translation.Game.Length > 0 && translation.Files.Count > 0)
                catalog.Translations.Add(translation);
        }
        return catalog;
    }

    /// <summary>A translation's info.json, from the copy kept by its SHA-1 or from GitHub.</summary>
    private static async Task<SharedTranslation?> ReadInfoAsync(string path, SharedFile info, CancellationToken cancel)
    {
        string cached = System.IO.Path.Combine(Root, "info", info.Sha + ".json");
        try
        {
            byte[]? data = File.Exists(cached) ? await File.ReadAllBytesAsync(cached, cancel) : null;
            if (data == null || GitSha(data) != info.Sha)
            {
                data = await DownloadAsync(path + "/" + InfoFile, info.Sha, cancel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cached)!);
                await File.WriteAllBytesAsync(cached, data, cancel);
            }
            return JsonSerializer.Deserialize<SharedTranslation>(data, s_json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A file of translations/ from raw.githubusercontent.com, checked against its SHA-1.</summary>
    private static async Task<byte[]> DownloadAsync(string path, string sha, CancellationToken cancel)
    {
        string url = $"https://raw.githubusercontent.com/{Repository}/{Branch}/{Folder}/"
            + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        byte[] data = await Http.GetByteArrayAsync(url, cancel);
        if (GitSha(data) != sha)
            throw new InvalidDataException($"{path} did not come whole (or changed on GitHub meanwhile)");
        return data;
    }

    /// <summary>Git's SHA-1 of a file ("blob {length}\0" and its bytes), as the tree gives it.</summary>
    public static string GitSha(ReadOnlySpan<byte> data)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha.AppendData(header);
        sha.AppendData(data);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    // ---- Translations downloaded

    /// <summary>The folder a shared translation is kept in.</summary>
    public static string LocalFolder(string path) =>
        System.IO.Path.Combine([Root, .. path.Split('/').Select(PlayerFolders.FolderName)]);

    /// <summary>The translations downloaded for a game (also offline): as they were when downloaded.</summary>
    public static List<SharedTranslation> Installed(string game)
    {
        var found = new List<SharedTranslation>();
        if (!Directory.Exists(Root))
            return found;
        foreach (var file in Directory.EnumerateFiles(Root, InstalledFile, SearchOption.AllDirectories))
            if (ReadInstalled(file) is { } t && string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase))
                found.Add(t);
        return found;
    }

    /// <summary>The translation downloaded from <paramref name="path"/>, or null.</summary>
    public static SharedTranslation? Installed(string game, string path) =>
        ReadInstalled(System.IO.Path.Combine(LocalFolder(path), InstalledFile)) is { } t && string.Equals(t.Game, game, StringComparison.OrdinalIgnoreCase) ? t : null;

    private static SharedTranslation? ReadInstalled(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<SharedTranslation>(File.ReadAllText(file), s_json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The bytes still to download for <paramref name="translation"/> (all of it when it is not there; 0: up to date).</summary>
    public static long PendingBytes(SharedTranslation translation)
    {
        var have = Installed(translation.Game, translation.Path)?.Files.ToDictionary(f => f.Name, f => f.Sha, StringComparer.Ordinal) ?? [];
        return translation.Files.Where(f => !have.TryGetValue(f.Name, out var sha) || sha != f.Sha).Sum(f => f.Size);
    }

    /// <summary>The translation downloaded and not as the catalog has it now.</summary>
    public static bool Outdated(SharedTranslation translation)
    {
        var have = Installed(translation.Game, translation.Path);
        return have != null && (PendingBytes(translation) > 0
            || have.Files.Any(f => !translation.Files.Any(n => n.Name == f.Name)));
    }

    /// <summary>
    /// Downloads <paramref name="translation"/> (the files it lacks or whose SHA-1 changed), puts the
    /// files in place once all have come, and removes the ones it no longer has.
    /// <paramref name="progress"/> gets the bytes downloaded so far.
    /// </summary>
    public static async Task InstallAsync(SharedTranslation translation, IProgress<long>? progress = null, CancellationToken cancel = default)
    {
        string folder = LocalFolder(translation.Path);
        Directory.CreateDirectory(folder);
        var have = Installed(translation.Game, translation.Path)?.Files.ToDictionary(f => f.Name, f => f.Sha, StringComparer.Ordinal) ?? [];
        var parts = new List<(string Part, string Target)>();
        long done = 0;
        try
        {
            foreach (var file in translation.Files)
            {
                string target = System.IO.Path.Combine(folder, PlayerFolders.FolderName(file.Name));
                if (have.TryGetValue(file.Name, out var sha) && sha == file.Sha && File.Exists(target))
                    continue;
                byte[] data = await DownloadAsync(translation.Path + "/" + file.Name, file.Sha, cancel);
                string part = target + ".part";
                await File.WriteAllBytesAsync(part, data, cancel);
                parts.Add((part, target));
                done += data.Length;
                progress?.Report(done);
            }
            foreach (var (part, target) in parts)
                File.Move(part, target, overwrite: true);
        }
        finally
        {
            foreach (var (part, _) in parts)
                if (File.Exists(part))
                    File.Delete(part);
        }
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string name = System.IO.Path.GetFileName(file);
            if (name != InstalledFile && !translation.Files.Any(f => PlayerFolders.FolderName(f.Name) == name))
                File.Delete(file);
        }
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder, InstalledFile), JsonSerializer.Serialize(translation, s_json), cancel);
    }

    /// <summary>Deletes a downloaded translation.</summary>
    public static void Remove(string path)
    {
        string folder = LocalFolder(path);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    // ---- The choices

    private static string ChoicesFile => System.IO.Path.Combine(Root, "choices.json");

    /// <summary>What was chosen for each game (by its scheme's name).</summary>
    public static Dictionary<string, TranslationChoice> LoadChoices()
    {
        try
        {
            if (File.Exists(ChoicesFile) && JsonSerializer.Deserialize<Dictionary<string, TranslationChoice>>(File.ReadAllText(ChoicesFile), s_json) is { } choices)
                return new Dictionary<string, TranslationChoice>(choices, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    public static TranslationChoice ChoiceFor(string game) =>
        LoadChoices().TryGetValue(game, out var choice) ? choice : new TranslationChoice();

    public static void SaveChoice(string game, TranslationChoice choice)
    {
        var choices = LoadChoices();
        choices[game] = choice;
        Directory.CreateDirectory(Root);
        File.WriteAllText(ChoicesFile, JsonSerializer.Serialize(choices, s_json));
    }

    /// <summary>
    /// The folder the game reads its translation from (GameData.TranslationFolder): null for the
    /// game folder's own translation\ (nothing chosen yet, or "local"), "" for none (the original
    /// chosen), else the shared translation's (when it is still downloaded).
    /// </summary>
    public static string? FolderFor(string game)
    {
        string choice = ChoiceFor(game).Choice;
        if (choice == TranslationChoice.Original)
            return "";
        if (choice.Length == 0 || choice == TranslationChoice.Local)
            return null;
        return Installed(game, choice) != null ? LocalFolder(choice) : null;
    }
}

/// <summary>The player's version, as its build gives it ("0.8.8", "0.8.9-dev.3").</summary>
public static class AppVersion
{
    public static string Text { get; } = Read();

    private static string Read()
    {
        string? version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
            return "0.0.0";
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>A build of a release (no "-dev" or other suffix): the one that looks for newer releases.</summary>
    public static bool IsRelease => !Text.Contains('-');

    /// <summary>The numbers of a version ("v0.8.6.1" -> 0, 8, 6, 1), for comparing.</summary>
    public static int[] Numbers(string version)
    {
        version = version.TrimStart('v', 'V');
        int dash = version.IndexOf('-');
        if (dash >= 0)
            version = version[..dash];
        return version.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
    }

    /// <summary>Whether <paramref name="other"/> is a newer version than this build's.</summary>
    public static bool IsNewer(string other)
    {
        int[] a = Numbers(other), b = Numbers(Text);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y)
                return x > y;
        }
        return false;
    }
}
