// schemeimport dump <Formats.dat>: the ShiinaRio part of GARbro's scheme database (BinaryFormatter,
// read with System.Formats.Nrbf, no GARbro types needed): scheme names, versions, extra crypts
// and their keys.
// schemeimport merge <Formats.dat> <our Formats.Json> <out.json>: our schemes (kept as they are:
// some are ours only, Nyaru Plus has a DecodeBin GARbro gets wrong) and every ShiinaRio scheme of
// the database we do not have, in our JSON form: byte arrays several schemes share once in
// SharedData ("@name" in a scheme), the common parts of the ShiinaImage once in CommonImages
// (base64), each scheme's own tail with it (FormatManager reads it). Our Formats.Json may be in
// that form or the older one (ShiinaImage/Common.bin and *.tail files next to it); a merge of
// its own result with the same Formats.dat gives the same file again.
//   dotnet run --project tools/SchemeImport -c Release -- merge <GARbro>\ArcFormats\Resources\Formats.dat
//       src/OpenShiina.Core/Data/Formats.Json new.json
using System.Formats.Nrbf;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

var dat = File.OpenRead(args[1]);
var header = new byte[8];
dat.ReadExactly(header);
if (Encoding.ASCII.GetString(header) != "GARbroDB")
    throw new InvalidDataException("not a GARbro scheme database");
var versionBytes = new byte[4];
dat.ReadExactly(versionBytes);
int datVersion = BitConverter.ToInt32(versionBytes);
var unpacked = new MemoryStream();
using (var z = new ZLibStream(dat, CompressionMode.Decompress))
    z.CopyTo(unpacked);
unpacked.Position = 0;
var root = (ClassRecord)NrbfDecoder.Decode(unpacked, out var records);
Console.Error.WriteLine($"Formats.dat version {datVersion}, root {root.TypeName.FullName}");

// A member by its name, or by "Type+name" for fields of a base class
string? Member(ClassRecord r, string name) =>
    r.MemberNames.FirstOrDefault(m => m == name || m == $"<{name}>k__BackingField" || m.EndsWith("+" + name) || m.EndsWith($"+<{name}>k__BackingField"));

object? Raw(ClassRecord r, string name) => Member(r, name) is { } m ? r.GetRawValue(m) : null;
ClassRecord? Class(ClassRecord r, string name) => Member(r, name) is { } m ? r.GetClassRecord(m) : null;

// Dictionary<string, X> serialised as ISerializable: KeyValuePairs
IEnumerable<(string Key, object? Value)> Pairs(ClassRecord dict)
{
    var kv = (SZArrayRecord<SerializationRecord>)dict.GetArrayRecord("KeyValuePairs")!;
    foreach (var item in kv.GetArray())
    {
        if (item is not ClassRecord pair)
            continue;
        var raw = pair.GetRawValue("value");
        yield return (pair.GetString("key")!, raw is PrimitiveTypeRecord<string> p ? p.Value : raw);
    }
}

var schemeMap = Class(root, "SchemeMap")!;
ClassRecord? war = null;
foreach (var (key, value) in Pairs(schemeMap))
    if (key == "WAR")
        war = (ClassRecord)value!;
if (war == null)
    throw new InvalidDataException("no WAR scheme");
var known = (SZArrayRecord<SerializationRecord>)war.GetArrayRecord(Member(war, "KnownSchemes")!)!;
var schemes = known.GetArray().Where(s => s != null).Cast<ClassRecord>().ToList();

string Str(ClassRecord r, string name) => Raw(r, name) as string ?? (Raw(r, name) is SerializationRecord s && s is PrimitiveTypeRecord<string> p ? p.Value : "");
int Int(ClassRecord r, string name) => Raw(r, name) is int i ? i : 0;
byte[]? Bytes(ClassRecord r, string name) => Member(r, name) is { } m && r.GetArrayRecord(m) is SZArrayRecord<byte> a ? a.GetArray() : null;
uint[]? UInts(ClassRecord r, string name) => Member(r, name) is { } m && r.GetArrayRecord(m) is SZArrayRecord<uint> a ? a.GetArray() : null;

if (args[0] == "dump")
{
    Console.WriteLine($"{schemes.Count} ShiinaRio schemes");
    foreach (var s in schemes)
    {
        var x = Class(s, "ExtraCrypt");
        var img = Class(s, "ShiinaImage");
        Console.WriteLine($"  {Str(s, "Name")} | {Str(s, "OriginalTitle")} | v{Int(s, "Version")} | names {Int(s, "EntryNameSize")} | extra {(x == null ? "-" : x.TypeName.Name + " [" + string.Join(",", x.MemberNames) + "]")} | image {(img == null ? "-" : img.TypeName.Name + " [" + string.Join(",", img.MemberNames) + "]")}");
    }
    foreach (var s in schemes)
        if (Class(s, "ExtraCrypt") is { } xc)
        {
            var seeds = xc.MemberNames.Where(m => m.EndsWith("Seed") || m.EndsWith("DecodeTable") || m.EndsWith("EncryptedSize") || m.EndsWith("PostDataOffset"))
                .GroupBy(m => m[(m.LastIndexOf('+') + 1)..])
                .Select(g => g.Key + "=" + string.Join("/", g.Select(m => xc.GetRawValue(m) is { } v && v is not SerializationRecord ? v.ToString() : (xc.GetArrayRecord(m) is SZArrayRecord<byte> b ? $"{b.Length}b:{Convert.ToHexString(SHA256.HashData(b.GetArray()))[..8]}" : "null"))));
            if (seeds.Any())
                Console.WriteLine($"  {Str(s, "Name")} {xc.TypeName.Name}: {string.Join(" ", seeds)}");
        }
    var datGameMap = Class(root, "GameMap")!;
    var titles = schemes.Select(s => Str(s, "Name")).ToHashSet();
    foreach (var p in Pairs(datGameMap).Take(15)) Console.WriteLine($"  map {p.Key} -> {p.Value as string}");
    Console.WriteLine($"game map: {Pairs(datGameMap).Count()} entries; for ShiinaRio: {Pairs(datGameMap).Count(p => p.Value is string v && titles.Contains(v))}");
    return;
}

// ---- merge ----
var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var ours = JsonNode.Parse(File.ReadAllText(args[2]))!.AsObject();
string ourDir = Path.GetDirectoryName(Path.GetFullPath(args[2]))!;
var ourSchemes = ours["SchemeMap"]!["WAR"]!["KnownSchemes"]!.AsArray().Select(n => n!.AsObject()).ToList();

// The common parts by content: name -> bytes, sha -> name
var commons = new Dictionary<string, byte[]>();
var commonBySha = new Dictionary<string, string>();
string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
string CommonName(byte[] data)
{
    string sha = Sha(data);
    if (commonBySha.TryGetValue(sha, out var name))
        return name;
    name = commons.Count == 0 ? "Common" : $"Common{commons.Count + 1}";
    commons[name] = data;
    commonBySha[sha] = name;
    return name;
}

JsonObject Image(byte[] common, int commonLength, byte[] tail)
{
    var whole = common.AsSpan(0, commonLength).ToArray().Concat(tail).ToArray();
    // An image that starts with a common part already kept is that part and the rest as its tail
    // (GARbro keeps some whole, common and tail in one array)
    foreach (var (known, data) in commons.OrderByDescending(c => c.Value.Length))
        if (whole.Length >= data.Length && whole.AsSpan(0, data.Length).SequenceEqual(data))
        {
            common = data;
            commonLength = data.Length;
            tail = whole[data.Length..];
            break;
        }
    var o = new JsonObject
    {
        ["$type"] = "GameRes.Formats.ShiinaRio.ImageArray",
        ["Common"] = CommonName(common),
        ["CommonLength"] = commonLength,
    };
    if (tail.Length > 0)
        o["Tail"] = Convert.ToBase64String(tail);
    o["Length"] = whole.Length;
    o["Sha256"] = Sha(whole);
    return o;
}

var result = new List<JsonObject>();
var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
// Ours first, with their files read in
// Ours may be in this form already (SharedData, CommonImages) or the older one (ShiinaImage files)
var ourShared = ours["SharedData"]?.AsObject();
var ourCommons = ours["CommonImages"]?.AsObject();
foreach (var s in ourSchemes)
{
    var copy = JsonNode.Parse(s.ToJsonString())!.AsObject();
    foreach (var field in new[] { "CryptKey", "Region", "DecodeBin" })
        if (copy[field]?.GetValue<string>() is { } v && v.StartsWith('@'))
            copy[field] = ourShared![v[1..]]!.GetValue<string>();
    if (copy["ExtraCrypt"] is JsonObject ec && ec["DecodeTable"]?.GetValue<string>() is { } dt && dt.StartsWith('@'))
        ec["DecodeTable"] = ourShared![dt[1..]]!.GetValue<string>();
    if (copy["ShiinaImage"] is JsonObject img)
    {
        string commonRef = img["Common"]!.GetValue<string>();
        var common = ourCommons?[commonRef] is { } c ? Convert.FromBase64String(c.GetValue<string>()) : File.ReadAllBytes(Path.Combine(ourDir, commonRef));
        int length = img["CommonLength"]?.GetValue<int>() ?? common.Length;
        var tail = img["Tail"] is not { } t ? [] : ourCommons != null ? Convert.FromBase64String(t.GetValue<string>()) : File.ReadAllBytes(Path.Combine(ourDir, t.GetValue<string>()));
        var embedded = Image(common, length, tail);
        if (embedded["Sha256"]!.GetValue<string>() != img["Sha256"]!.GetValue<string>())
            throw new InvalidDataException($"{copy["Name"]}: the image does not match its Sha256");
        copy["ShiinaImage"] = embedded;
    }
    result.Add(copy);
    names.Add(copy["Name"]!.GetValue<string>());
}
int ourCount = result.Count;

string Hex(byte[]? b) => b == null ? "" : Convert.ToHexString(b);
JsonNode? Value(ClassRecord r, string member)
{
    var raw = r.GetRawValue(member);
    switch (raw)
    {
        case null: return null;
        case uint u: return JsonValue.Create(u);
        case int i: return JsonValue.Create(i);
        case bool b: return JsonValue.Create(b);
        case string str: return JsonValue.Create(str);
    }
    var array = r.GetArrayRecord(member);
    return array switch
    {
        SZArrayRecord<byte> bytes => JsonValue.Create(Hex(bytes.GetArray())),
        SZArrayRecord<uint> uints => new JsonArray(uints.GetArray().Select(x => (JsonNode)JsonValue.Create($"0x{x:X8}")!).ToArray()),
        _ => throw new NotSupportedException($"{r.TypeName.Name}.{member}: {raw.GetType().Name}"),
    };
}

foreach (var s in schemes)
{
    string name = Str(s, "Name");
    if (names.Contains(name))
        continue;
    var o = new JsonObject
    {
        ["$type"] = "GameRes.Formats.ShiinaRio.EncryptionScheme",
        ["Name"] = name,
        ["OriginalTitle"] = Str(s, "OriginalTitle"),
        ["Version"] = Int(s, "Version"),
        ["EntryNameSize"] = Int(s, "EntryNameSize"),
        ["CryptKey"] = Bytes(s, "CryptKey") is { } ck ? Hex(ck) : null,
        ["HelperKey"] = UInts(s, "HelperKey") is { } hk ? new JsonArray(hk.Select(x => (JsonNode)JsonValue.Create($"0x{x:X8}")!).ToArray()) : null,
        ["Region"] = Bytes(s, "Region") is { } rg ? Hex(rg) : null,
        ["DecodeBin"] = Bytes(s, "DecodeBin") is { } db ? Hex(db) : null,
    };
    if (Class(s, "ShiinaImage") is { } img)
    {
        var common = Bytes(img, "m_common") ?? [];
        var tail = Bytes(img, "m_extra") ?? [];
        o["ShiinaImage"] = Image(common, Int(img, "m_common_length"), tail);
    }
    else
        o["ShiinaImage"] = null;
    if (Class(s, "ExtraCrypt") is { } x)
    {
        var e = new JsonObject { ["$type"] = "GameRes.Formats.ShiinaRio." + x.TypeName.Name };
        foreach (var m in x.MemberNames)
        {
            string field = m[(m.LastIndexOf('+') + 1)..];
            if (field.StartsWith("m_"))
                field = char.ToUpperInvariant(field[2]) + field[3..];
            if (e.ContainsKey(field))
                continue;
            if (Value(x, m) is { } v)
                e[field] = v;
        }
        o["ExtraCrypt"] = e;
    }
    else
        o["ExtraCrypt"] = null;
    result.Add(o);
    names.Add(name);
}

// Byte arrays that several schemes share (one Region for all, a few DecodeBins and CryptKeys, as
// the database shares them) are kept once in SharedData; a scheme names them "@name"
var shared = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var field in new[] { "CryptKey", "Region", "DecodeBin" })
{
    var counts = result.Select(r => r[field]?.GetValue<string>()).Where(v => !string.IsNullOrEmpty(v))
        .GroupBy(v => v!).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
    for (int i = 0; i < counts.Count; i++)
    {
        string name = i == 0 ? field : $"{field}{i + 1}";
        shared[name] = counts[i];
        foreach (var r in result)
            if (r[field]?.GetValue<string>() == counts[i])
                r[field] = "@" + name;
    }
}
{
    var crypts = result.Select(r => r["ExtraCrypt"] as JsonObject).Where(x => x?["DecodeTable"] != null).ToList();
    var tables = crypts.GroupBy(x => x!["DecodeTable"]!.GetValue<string>()).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
    for (int i = 0; i < tables.Count; i++)
    {
        string name = i == 0 ? "DecodeTable" : $"DecodeTable{i + 1}";
        shared[name] = tables[i];
        foreach (var x in crypts)
            if (x!["DecodeTable"]!.GetValue<string>() == tables[i])
                x["DecodeTable"] = "@" + name;
    }
}

var gameMap = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var (key, value) in Pairs(Class(root, "GameMap")!))
    if (value is string v && names.Contains(v))
        gameMap[key] = v;
foreach (var kv in ours["GameMap"]!.AsObject())
    gameMap[kv.Key] = kv.Value!.GetValue<string>();

var outJson = new JsonObject
{
    ["Version"] = Math.Max(datVersion, ours["Version"]!.GetValue<int>()),
    ["SchemeCount"] = 1,
    ["GameMapCount"] = gameMap.Count,
    ["SharedData"] = new JsonObject(shared.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
    ["CommonImages"] = new JsonObject(commons.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(Convert.ToBase64String(c.Value))))),
    ["SchemeMap"] = new JsonObject
    {
        ["WAR"] = new JsonObject
        {
            ["$type"] = "GameRes.Formats.ShiinaRio.WarcScheme",
            ["KnownSchemes"] = new JsonArray(result.OrderBy(r => r["Name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase).Select(r => (JsonNode)r).ToArray()),
        },
    },
    ["GameMap"] = new JsonObject(gameMap.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
};
File.WriteAllText(args[3], outJson.ToJsonString(opts), new UTF8Encoding(false));
Console.WriteLine($"{ourCount} of ours, {result.Count - ourCount} added from Formats.dat v{datVersion}, {commons.Count} common images ({string.Join(", ", commons.Select(c => $"{c.Key} {c.Value.Length} bytes"))}), {gameMap.Count} game map entries");
var types = result.Select(r => (r["ExtraCrypt"] as JsonObject)?["$type"]?.GetValue<string>() ?? "-").GroupBy(t => t).Select(g => $"{g.Key[(g.Key.LastIndexOf('.') + 1)..]} {g.Count()}");
Console.WriteLine("extra crypts: " + string.Join(", ", types));
Console.WriteLine("shared data: " + string.Join(", ", shared.Select(c => $"{c.Key} {c.Value.Length / 2} bytes")));
