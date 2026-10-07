// FormatManager: loads the per-game encryption schemes from Formats.Json together with the
// ShiinaImage files they reference (the shared ShiinaImage/Common.bin plus optional per-game
// ShiinaImage/*.tail), and maps game executables to schemes for auto-detection.

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenShiina.Archives;

public class FormatManager
{
    public const string SchemeFileName = "Formats.Json";

    private static readonly Lazy<FormatManager> s_instance = new(() => new FormatManager());
    public static FormatManager Instance => s_instance.Value;

    public Dictionary<string, string> GameMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EncryptionScheme> Schemes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EncryptionScheme> KnownSchemes { get; } = new();

    /// <summary>Path of the loaded Formats.Json, or null if none was found.</summary>
    public string? SchemeFilePath { get; private set; }

    /// <summary>Problems found while loading schemes; shown to the user by the UI.</summary>
    public List<string> LoadErrors { get; } = new();

    public static readonly string[] GrandCrossGames = new[]
    {
        "Azu Plus",
        "ERO-ON",
        "Homu Plus",
        "Kuroneko Plus",
        "Maki Fes!",
        "Nyaru Plus",
        "Oreimo Plus",
        "Re: Rem Plus",
        "Rikka Plus",
        "Sena Plus",
        "Yuru Plus"
    };

    public FormatManager()
    {
        SchemeFilePath = FindSchemeFile();
        if (SchemeFilePath == null)
        {
            LoadErrors.Add($"{SchemeFileName} was not found next to the application.");
            return;
        }
        try
        {
            Load(SchemeFilePath);
        }
        catch (Exception ex)
        {
            LoadErrors.Add($"Failed to read {SchemeFilePath}: {ex.Message}");
        }
    }

    private static string? FindSchemeFile()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, SchemeFileName),
            Path.Combine(Directory.GetCurrentDirectory(), SchemeFileName),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private void Load(string jsonPath)
    {
        string baseDir = Path.GetDirectoryName(jsonPath)!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        var root = doc.RootElement;

        if (root.TryGetProperty("GameMap", out var gameMap))
        {
            foreach (var kv in gameMap.EnumerateObject())
                GameMap[kv.Name] = kv.Value.GetString() ?? "";
        }

        if (!root.TryGetProperty("SchemeMap", out var schemeMap))
            throw new InvalidDataException("missing 'SchemeMap'");

        foreach (var format in schemeMap.EnumerateObject())
        {
            if (!format.Value.TryGetProperty("KnownSchemes", out var known))
                continue;
            foreach (var item in known.EnumerateArray())
            {
                string name = GetString(item, "Name");
                try
                {
                    var scheme = ParseScheme(item, baseDir);
                    Schemes[scheme.Name] = scheme;
                    KnownSchemes.Add(scheme);
                }
                catch (Exception ex)
                {
                    LoadErrors.Add($"Scheme '{name}' skipped: {ex.Message}");
                }
            }
        }
    }

    // Shared ShiinaImage arrays (e.g. ShiinaImage/Common.bin), loaded once and reused by every scheme
    private readonly Dictionary<string, byte[]> m_commonImages = new(StringComparer.OrdinalIgnoreCase);

    private EncryptionScheme ParseScheme(JsonElement e, string baseDir)
    {
        return new EncryptionScheme
        {
            Name = GetString(e, "Name"),
            OriginalTitle = GetString(e, "OriginalTitle"),
            Version = e.GetProperty("Version").GetInt32(),
            EntryNameSize = e.GetProperty("EntryNameSize").GetInt32(),
            CryptKey = GetHex(e, "CryptKey"),
            HelperKey = GetHexUInts(e, "HelperKey"),
            Region = GetHex(e, "Region"),
            DecodeBin = GetHex(e, "DecodeBin"),
            ShiinaImage = LoadShiinaImage(e, baseDir),
            ExtraCrypt = CreateExtraCrypt(e),
        };
    }

    /// <summary>
    /// ShiinaImage = first "CommonLength" bytes of the shared "Common" file + optional per-game "Tail"
    /// (GARbro's ImageArray layout). The legacy single-file form { "File": ... } is still accepted.
    /// "Length" and "Sha256" always describe the assembled image.
    /// </summary>
    private IByteArray? LoadShiinaImage(JsonElement scheme, string baseDir)
    {
        if (!scheme.TryGetProperty("ShiinaImage", out var img) || img.ValueKind == JsonValueKind.Null)
            return null;

        byte[] common;
        int commonLength;
        byte[] tail = Array.Empty<byte>();
        if (img.TryGetProperty("Common", out var commonProp))
        {
            string commonPath = ResolveDataFile(baseDir, commonProp.GetString()!);
            if (!m_commonImages.TryGetValue(commonPath, out common!))
                m_commonImages[commonPath] = common = File.ReadAllBytes(commonPath);
            commonLength = img.TryGetProperty("CommonLength", out var cl) ? cl.GetInt32() : common.Length;
            if (commonLength > common.Length)
                throw new InvalidDataException($"{Path.GetFileName(commonPath)} is {common.Length} bytes, CommonLength is {commonLength}");
            if (img.TryGetProperty("Tail", out var tailProp))
                tail = File.ReadAllBytes(ResolveDataFile(baseDir, tailProp.GetString()!));
        }
        else if (img.TryGetProperty("File", out var fileProp))
        {
            common = File.ReadAllBytes(ResolveDataFile(baseDir, fileProp.GetString()!));
            commonLength = common.Length;
        }
        else
        {
            throw new InvalidDataException("ShiinaImage has neither 'Common' nor 'File'");
        }

        var image = new ImageArray(common, commonLength, tail);
        if (img.TryGetProperty("Length", out var len) && len.GetInt32() != image.Length)
            throw new InvalidDataException($"ShiinaImage is {image.Length} bytes, expected {len.GetInt32()}");
        if (img.TryGetProperty("Sha256", out var sha))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(common, 0, commonLength);
            hash.AppendData(tail);
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ShiinaImage SHA-256 does not match Formats.Json");
        }
        return image;
    }

    private static string ResolveDataFile(string baseDir, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path))
            throw new FileNotFoundException($"ShiinaImage file not found: {path}");
        return path;
    }

    private static IDecryptExtra? CreateExtraCrypt(JsonElement scheme)
    {
        if (!scheme.TryGetProperty("ExtraCrypt", out var x) || x.ValueKind == JsonValueKind.Null)
            return null;

        string fullType = GetString(x, "$type");
        string type = fullType.Substring(fullType.LastIndexOf('.') + 1);
        uint Seed() => x.GetProperty("Seed").GetUInt32();
        byte[] Table() => GetHex(x, "DecodeTable") ?? throw new InvalidDataException($"{type} has no DecodeTable");

        return type switch
        {
            nameof(YuruPlusCrypt)  => new YuruPlusCrypt(Seed(), Table()),
            nameof(MakiFesCrypt)   => new MakiFesCrypt(Seed(), Table()),
            nameof(KeyAdlerCrypt)  => new KeyAdlerCrypt(Seed()),
            nameof(ShojoMamaCrypt) => new ShojoMamaCrypt(Seed(), Table()),
            nameof(TestamentCrypt) => new TestamentCrypt(Seed(), Table()),
            nameof(NyaruCrypt)     => new NyaruCrypt(),
            nameof(MajimeCrypt)    => new MajimeCrypt(),
            nameof(JokersCrypt)    => new JokersCrypt(),
            nameof(AlcotCrypt)     => new AlcotCrypt(),
            nameof(DodakureCrypt)  => new DodakureCrypt(),
            _ => throw new NotSupportedException($"ExtraCrypt type '{fullType}' is not implemented"),
        };
    }

    public EncryptionScheme? GetScheme(string name)
    {
        if (Schemes.TryGetValue(name, out var s))
            return s;
        return Schemes.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public string? LookupGame(string arcPath)
    {
        string dir = Path.GetDirectoryName(arcPath) ?? "";
        if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();

        // Check if known game exe exists in same directory (any case: the folder may be on Linux)
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string?>();
        foreach (var kvp in GameMap)
        {
            if (files.Contains(kvp.Key))
                return kvp.Value;
        }

        // Check archive file name against game map or scheme names
        string fileName = Path.GetFileNameWithoutExtension(arcPath);
        foreach (var kvp in GameMap)
        {
            if (fileName.Contains(Path.GetFileNameWithoutExtension(kvp.Key), StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        foreach (var scheme in Schemes.Keys)
        {
            if (fileName.Contains(scheme, StringComparison.OrdinalIgnoreCase))
                return scheme;
        }

        return null;
    }

    private static string GetString(JsonElement e, string name)
    {
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static byte[]? GetHex(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        return Convert.FromHexString(v.GetString()!);
    }

    private static uint[]? GetHexUInts(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return v.EnumerateArray().Select(t =>
        {
            string s = t.GetString()!.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }).ToArray();
    }
}
