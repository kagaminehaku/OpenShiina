// What both tabs use: the file and folder pickers, messages, the list of schemes, sizes as text.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpenShiina.App;
using OpenShiina.Archives;

namespace OpenShiina.Tools;

internal static class Ui
{
    /// <summary>The first entry of the scheme lists: the game found by its .exe, else by its keys.</summary>
    public const string AutoScheme = "Auto (the game's .exe, else its keys)";

    /// <summary>Auto, then the schemes: the Grand Cross games first, the others by name.</summary>
    public static List<string> SchemeNames()
    {
        var formats = FormatManager.Instance;
        var names = new List<string> { AutoScheme };
        names.AddRange(FormatManager.GrandCrossGames.Where(g => formats.GetScheme(g) != null));
        names.AddRange(formats.KnownSchemes.Select(s => s.Name).Where(n => !FormatManager.GrandCrossGames.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return names;
    }

    /// <summary>The scheme chosen in a list made by <see cref="SchemeNames"/>, or null for Auto.</summary>
    public static EncryptionScheme? Chosen(ComboBox schemes) =>
        schemes.SelectedIndex > 0 && schemes.SelectedItem is string name ? FormatManager.Instance.GetScheme(name) : null;

    public static Task Message(Control owner, string text, string caption = "OpenShiina Tools") =>
        MessageDialog.ShowAsync(TopLevel.GetTopLevel(owner), text, caption, 0);

    /// <summary>A question with Yes and No: true for Yes.</summary>
    public static async Task<bool> Ask(Control owner, string text, string caption = "OpenShiina Tools") =>
        await MessageDialog.ShowAsync(TopLevel.GetTopLevel(owner), text, caption, 4) == 6;

    public static async Task<string?> PickFile(Control owner, string title, params (string Name, string[] Patterns)[] types)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
            return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [.. types.Select(t => new FilePickerFileType(t.Name) { Patterns = t.Patterns }), FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFolder(Control owner, string title, string? near = null)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
            return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (near != null && Directory.Exists(near))
        {
            try
            {
                options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(near);
            }
            catch (Exception)
            {
                // No folder to start from
            }
        }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickSave(Control owner, string title, string suggested)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
            return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
