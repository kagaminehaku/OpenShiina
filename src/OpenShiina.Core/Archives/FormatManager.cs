// FormatManager: loads the per-game encryption schemes from Formats.Json and maps game
// executables to schemes for auto-detection. Formats.Json is GARbro's scheme database (its
// ShiinaRio part, Formats.dat as JSON) with what it shares kept once, as Formats.dat does:
//   "SharedData":   byte arrays several schemes use (Region, DecodeBins, a CryptKey), as hex;
//                   a scheme's field names one as "@name" instead of giving its hex
//   "CommonImages": the parts of the ShiinaImage that games share, as base64; a scheme's
//                   ShiinaImage is the first "CommonLength" bytes of its "Common" and its own
//                   "Tail" (base64), checked by "Length" and "Sha256"

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

    /// <summary>
    /// Where Formats.Json is when it is not next to the program (Android keeps it in its package:
    /// the app copies it out to a folder first); set it before
    /// <see cref="Instance"/> is first used.
    /// </summary>
    public static string? DataFolder { get; set; }

    private static string? FindSchemeFile()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(DataFolder ?? baseDir, SchemeFileName),
            Path.Combine(baseDir, SchemeFileName),
            Path.Combine(Directory.GetCurrentDirectory(), SchemeFileName),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private void Load(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        var root = doc.RootElement;

        if (root.TryGetProperty("SharedData", out var shared))
            foreach (var kv in shared.EnumerateObject())
                m_shared[kv.Name] = kv.Value.GetString() ?? "";
        if (root.TryGetProperty("CommonImages", out var commons))
            foreach (var kv in commons.EnumerateObject())
                m_commonImages[kv.Name] = Convert.FromBase64String(kv.Value.GetString() ?? "");

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
                    var scheme = ParseScheme(item);
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

    // What the schemes share: hex byte arrays by name, and the common parts of their ShiinaImage
    private readonly Dictionary<string, string> m_shared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> m_commonImages = new(StringComparer.Ordinal);

    private EncryptionScheme ParseScheme(JsonElement e)
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
            ShiinaImage = LoadShiinaImage(e),
            ExtraCrypt = CreateExtraCrypt(e),
        };
    }

    /// <summary>
    /// ShiinaImage = the first "CommonLength" bytes of a common part (CommonImages) + the scheme's
    /// own "Tail" (GARbro's ImageArray); "Length" and "Sha256" describe the image put together.
    /// </summary>
    private IByteArray? LoadShiinaImage(JsonElement scheme)
    {
        if (!scheme.TryGetProperty("ShiinaImage", out var img) || img.ValueKind == JsonValueKind.Null)
            return null;
        string commonName = GetString(img, "Common");
        if (!m_commonImages.TryGetValue(commonName, out var common))
            throw new InvalidDataException($"ShiinaImage names the common part '{commonName}', which CommonImages does not have");
        int commonLength = img.TryGetProperty("CommonLength", out var cl) ? cl.GetInt32() : common.Length;
        if (commonLength > common.Length)
            throw new InvalidDataException($"the common part '{commonName}' is {common.Length} bytes, CommonLength is {commonLength}");
        byte[] tail = img.TryGetProperty("Tail", out var tailProp) ? Convert.FromBase64String(tailProp.GetString() ?? "") : [];

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

    private IDecryptExtra? CreateExtraCrypt(JsonElement scheme)
    {
        if (!scheme.TryGetProperty("ExtraCrypt", out var x) || x.ValueKind == JsonValueKind.Null)
            return null;

        string fullType = GetString(x, "$type");
        string type = fullType.Substring(fullType.LastIndexOf('.') + 1);
        uint Seed() => x.GetProperty("Seed").GetUInt32();
        byte[] Table() => GetHex(x, "DecodeTable") ?? throw new InvalidDataException($"{type} has no DecodeTable");
        byte[] KeyBytes() => GetHex(x, "Key") ?? throw new InvalidDataException($"{type} has no Key");

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
            nameof(PostAdlerCrypt) => new PostAdlerCrypt(),
            nameof(PreAdlerCrypt)  => new PreAdlerCrypt(),
            nameof(BinboCrypt)     => new BinboCrypt(),
            nameof(CountCrypt)     => new CountCrypt(),
            nameof(AltCountCrypt)  => new AltCountCrypt(),
            nameof(UshimitsuCrypt) => new UshimitsuCrypt(x.GetProperty("Key").GetUInt32()),
            nameof(Nukitashi2Crypt) => new Nukitashi2Crypt(KeyBytes()),
            nameof(SaiminCrypt)    => new SaiminCrypt(KeyBytes()),
            _ => throw new NotSupportedException($"ExtraCrypt type '{fullType}' is not implemented"),
        };
    }

    public EncryptionScheme? GetScheme(string name)
    {
        if (Schemes.TryGetValue(name, out var s))
            return s;
        return Schemes.Values.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The scheme for the game the archive at <paramref name="arcPath"/> belongs to: the one its
    /// folder's .exe (or the archive's name) tells; for WARC 1.0 / 1.1 archives, which have no
    /// keys, one without keys named after the folder when nothing tells. Null when none fits.
    /// </summary>
    public EncryptionScheme? SchemeForArchive(string arcPath)
    {
        if (LookupGame(arcPath) is { } title && GetScheme(title) is { } scheme)
            return scheme;
        if (WarcOpener.ArchiveVersion(arcPath) is { } version && WarcOpener.NeedsNoScheme(version))
            return EncryptionScheme.Warc110(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(arcPath))) ?? "ShiinaRio");
        return null;
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

    /// <summary>A byte array as hex, or "@name" of one in SharedData.</summary>
    private byte[]? GetHex(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;
        string hex = v.GetString()!;
        if (hex.StartsWith('@') && !m_shared.TryGetValue(hex[1..], out hex!))
            throw new InvalidDataException($"{name} names '{v.GetString()}', which SharedData does not have");
        return Convert.FromHexString(hex);
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
