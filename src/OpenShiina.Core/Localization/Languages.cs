// The language of the players' own screens: Localization/Strings.resx in English, the source, and
// the translations beside it (Strings.<code>.resx, from translators). The setting Language picks
// one, or follows the device's language (empty). All of them are in this assembly as
// OpenShiina.Localization.Strings[.<code>].resources; Text reads a word in the screens' language,
// then its parents' (vi-VN, vi), then English. Translated lists the codes that have a file: the
// build writes it from the files with the class Strings (OpenShiina.Core.csproj), so a new
// translation shows in the settings without a change to the code.

using System.Collections;
using System.Globalization;
using System.Resources;

namespace OpenShiina.Localization;

public static partial class Languages
{
    // The device's language, as the process started (before Apply changes it)
    private static readonly CultureInfo s_device = CultureInfo.CurrentUICulture;

    /// <summary>Shows the screens in <paramref name="code"/> ("en", "vi"...), or in the device's language for none (or an unknown code).</summary>
    public static void Apply(string? code)
    {
        var culture = s_device;
        if (!string.IsNullOrEmpty(code))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(code);
            }
            catch (CultureNotFoundException)
            {
                // The device's
            }
        }
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>The choices of the settings: the device's language first (""), then English and each translation, by their own names.</summary>
    public static IReadOnlyList<(string Code, string Name)> Choices()
    {
        var choices = new List<(string, string)> { ("", string.Format(Strings.LanguageSystem, Name(s_device))) };
        foreach (string code in Translated.Prepend("en").Distinct())
            choices.Add((code, Name(CultureInfo.GetCultureInfo(code))));
        return choices;
    }

    // Each language's words as read (null: no translation), "" for English
    private static readonly Dictionary<string, Dictionary<string, string>?> s_words = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The word <paramref name="name"/> (Strings.resx's) in the screens' language, else English.</summary>
    public static string Text(string name)
    {
        for (var culture = CultureInfo.CurrentUICulture; culture.Name.Length > 0; culture = culture.Parent)
            if (Words(culture.Name)?.TryGetValue(name, out string? word) == true)
                return word;
        return Words("")?.GetValueOrDefault(name) ?? name;
    }

    private static Dictionary<string, string>? Words(string language)
    {
        lock (s_words)
        {
            if (s_words.TryGetValue(language, out var words))
                return words;
            string resource = language.Length == 0 ? "OpenShiina.Localization.Strings.resources" : $"OpenShiina.Localization.Strings.{language}.resources";
            using var stream = typeof(Languages).Assembly.GetManifestResourceStream(resource);
            if (stream != null)
            {
                words = new Dictionary<string, string>(StringComparer.Ordinal);
                using var reader = new ResourceReader(stream);
                foreach (DictionaryEntry entry in reader)
                    if (entry.Value is string word)
                        words[(string)entry.Key] = word;
            }
            s_words[language] = words;
            return words;
        }
    }

    /// <summary>A language's name in itself, capitalised ("English", "Tiếng Việt").</summary>
    private static string Name(CultureInfo culture)
    {
        string name = culture.NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : culture.Name;
    }
}
