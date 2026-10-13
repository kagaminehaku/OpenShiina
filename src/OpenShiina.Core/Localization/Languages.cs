// The language of the players' own screens: Localization/Strings.resx in English, the source, and
// the translations beside it (Strings.<code>.resx, from translators). The setting Language picks
// one, or follows the device's language (empty). Translated lists the codes that have a file: the
// build writes it from the files (OpenShiina.Core.csproj), so a new translation shows in the
// settings without a change to the code.

using System.Globalization;

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

    /// <summary>A language's name in itself, capitalised ("English", "Tiếng Việt").</summary>
    private static string Name(CultureInfo culture)
    {
        string name = culture.NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : culture.Name;
    }
}
