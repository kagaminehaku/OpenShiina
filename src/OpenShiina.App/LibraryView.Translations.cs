// The home screen's part of the shared translations (TranslationService) and of new releases
// (AppUpdater).
//
// The first time a game that has shared translations is played, it asks how to play it: the
// original, the game folder's own translation (when it has a translation\ folder), or one of the
// shared ones, each said with its language, kind, progress, makers and size; any of them can be
// kept downloaded besides. The answer is remembered for the game; offline, or with no shared
// translation, nothing is asked (it is the next time online, when there are some). A shared
// translation that appears later is not asked about: the game's "…" gets a dot and "Translation…",
// which shows the same choice again. Downloaded translations updated in the background are told in
// a toast.
//
// A newer release is told once a run, when the library first shows: what changed and Update, Later
// (told again the next run) or Skip this version.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenShiina.Game;
using OpenShiina.Localization;

namespace OpenShiina.App;

public sealed partial class LibraryView
{
    private static bool s_lookedOnline;

    /// <summary>The update installed and the new program started: the host quits.</summary>
    public event Action? QuitForUpdate;

    /// <summary>Once a run: the downloaded translations brought up to date, a newer release told.</summary>
    private void LookOnline()
    {
        if (s_lookedOnline || !PlayerSettings.Load().CheckGitHub)
            return;
        s_lookedOnline = true;
        AppUpdater.CleanUp();
        TranslationService.Updated += translation => Dispatcher.UIThread.Post(() =>
            Toast(string.Format(Strings.TranslationUpdated, translation.Title, LanguageName(translation.Language), translation.Game), null));
        _ = Task.Run(TranslationService.RefreshAsync);
        _ = TellReleaseAsync();
    }

    // ---- A game's translation

    /// <summary>
    /// Before a game is played: the question of how to play it, when it has not been asked for
    /// this game and GitHub has translations of it. False when the user cancelled (nothing starts).
    /// </summary>
    public async Task<bool> TranslationBeforePlayAsync(string game, string folder)
    {
        if (SharedTranslations.ChoiceFor(game).Choice.Length > 0)
            return true;
        var catalog = await TranslationService.CatalogAsync(TimeSpan.FromSeconds(4));
        if (catalog == null || catalog.For(game).Count == 0)
            return true;
        return await ChooseTranslationAsync(game, folder, play: true);
    }

    /// <summary>
    /// How to play a game: the original, the game folder's translation or a shared one, and which
    /// shared ones to keep downloaded. True when chosen (and saved), false when cancelled.
    /// </summary>
    private async Task<bool> ChooseTranslationAsync(string game, string folder, bool play)
    {
        var catalog = await TranslationService.CatalogAsync(TimeSpan.FromSeconds(4));
        var shared = catalog?.For(game) ?? [];
        // (downloaded ones GitHub no longer has, or all of them offline)
        foreach (var installed in SharedTranslations.Installed(game))
            if (!shared.Any(t => t.Path == installed.Path))
                shared.Add(installed);
        shared = shared.OrderBy(t => t.Language, StringComparer.Ordinal).ThenByDescending(t => t.Progress).ToList();
        bool local = TranslationTables.Exist(Path.Combine(folder, TranslationTables.Folder));
        var choice = SharedTranslations.ChoiceFor(game);

        var done = new TaskCompletionSource<bool>();
        var rows = new StackPanel { Spacing = 10 };
        var original = new RadioButton { Content = Strings.TranslationOriginal, GroupName = "translation" };
        rows.Children.Add(original);
        RadioButton? localButton = null;
        if (local)
        {
            localButton = new RadioButton { Content = Option(Strings.TranslationLocal, Strings.TranslationLocalHelp, null), GroupName = "translation" };
            rows.Children.Add(localButton);
        }
        var options = new List<(SharedTranslation Translation, RadioButton Use, CheckBox Keep, bool Installed)>();
        foreach (var t in shared)
        {
            bool installed = SharedTranslations.Installed(game, t.Path) != null;
            bool known = catalog?.Translations.Contains(t) == true;
            long pending = known ? SharedTranslations.PendingBytes(t) : 0;
            string state = !installed ? Bytes(t.Size)
                : pending > 0 ? string.Format(Strings.TranslationUpdateWaiting, Bytes(pending))
                : Strings.TranslationDownloaded;
            string language = LanguageName(t.Language);
            string title = string.Equals(t.Title, language, StringComparison.CurrentCultureIgnoreCase) || t.Title.Length == 0 ? language : t.Title + " · " + language;
            var use = new RadioButton { Content = Option(title, Details(t), t.Notes), GroupName = "translation" };
            var keep = new CheckBox
            {
                Content = installed ? Strings.TranslationKeep : Strings.TranslationDownload,
                IsChecked = installed, IsEnabled = known || installed, Margin = new Thickness(28, 0, 0, 0),
            };
            ToolTip.SetTip(keep, state);
            var stateText = Muted(state);
            stateText.Margin = new Thickness(28, 0, 0, 0);
            use.IsCheckedChanged += (_, _) =>
            {
                if (use.IsChecked == true)
                    keep.IsChecked = true;
                keep.IsEnabled = use.IsChecked != true && (known || installed);
            };
            if (!known && !installed)
                use.IsEnabled = false;
            rows.Children.Add(new StackPanel { Spacing = 4, Children = { use, stateText, keep } });
            options.Add((t, use, keep, installed));
        }
        // What was chosen before, else the first shared translation in the screens' language, else the original
        string screens = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var before = options.FirstOrDefault(o => o.Translation.Path == choice.Choice).Use
            ?? (choice.Choice == TranslationChoice.Local ? localButton : null)
            ?? (choice.Choice == TranslationChoice.Original ? original : null)
            ?? options.FirstOrDefault(o => o.Translation.Language == screens && o.Use.IsEnabled).Use
            ?? original;
        before.IsChecked = true;

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        var cancel = new Button { Content = Strings.Cancel };
        var go = new Button { Content = play ? Strings.Play : Strings.Save };
        go.Classes.Add("accent");
        cancel.Click += (_, _) =>
        {
            CloseOverlay();
            done.TrySetResult(false);
        };
        go.Click += async (_, _) =>
        {
            go.IsEnabled = cancel.IsEnabled = rows.IsEnabled = false;
            status.IsVisible = true;
            try
            {
                foreach (var (t, use, keep, installed) in options)
                {
                    bool wanted = use.IsChecked == true || keep.IsChecked == true;
                    if (!wanted && installed)
                        SharedTranslations.Remove(t.Path);
                    else if (wanted && catalog?.Translations.Contains(t) == true && SharedTranslations.PendingBytes(t) > 0)
                    {
                        long size = SharedTranslations.PendingBytes(t);
                        status.Text = string.Format(Strings.TranslationDownloading, t.Title, Bytes(0), Bytes(size));
                        var progress = new Progress<long>(n => status.Text = string.Format(Strings.TranslationDownloading, t.Title, Bytes(n), Bytes(size)));
                        await SharedTranslations.InstallAsync(t, progress);
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                status.Text = string.Format(Strings.TranslationDownloadFailed, e.Message);
                go.IsEnabled = cancel.IsEnabled = rows.IsEnabled = true;
                return;
            }
            choice.Choice = original.IsChecked == true ? TranslationChoice.Original
                : localButton?.IsChecked == true ? TranslationChoice.Local
                : options.First(o => o.Use.IsChecked == true).Translation.Path;
            choice.Seen = shared.Select(t => t.Path).Union(choice.Seen).Distinct().ToList();
            SharedTranslations.SaveChoice(game, choice);
            CloseOverlay();
            done.TrySetResult(true);
            Choose(m_games.FirstOrDefault(g => g.Folder == m_chosen));
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, go } };
        string name = m_games.FirstOrDefault(g => g.Folder == folder)?.Name ?? game;
        var dialog = new Border
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(20),
            MaxWidth = 560, Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = string.Format(Strings.TranslationTitle, name), FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }
                        .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink),
                    Muted(play ? Strings.TranslationAskFirst : Strings.TranslationAskAgain),
                    rows, status, buttons,
                },
            },
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border);
        ShowOverlay(dialog, () => done.TrySetResult(false));
        return await done.Task;
    }

    /// <summary>A choice's name, a line about it and its notes.</summary>
    private static Control Option(string title, string line, string? notes)
    {
        var panel = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } } };
        if (line.Length > 0)
            panel.Children.Add(Muted(line));
        if (!string.IsNullOrWhiteSpace(notes))
            panel.Children.Add(Muted(notes));
        return panel;
    }

    /// <summary>A shared translation's kind, progress, makers and day.</summary>
    private static string Details(SharedTranslation t)
    {
        var parts = new List<string>
        {
            t.Kind switch
            {
                "machine" => Strings.TranslationMachine,
                "machine+edited" => Strings.TranslationMachineEdited,
                _ => Strings.TranslationHuman,
            },
        };
        if (t.Progress is > 0 and < 100)
            parts.Add(string.Format(Strings.TranslationProgress, t.Progress));
        if (t.Authors.Length > 0)
            parts.Add(string.Join(", ", t.Authors));
        if (t.Updated.Length > 0)
            parts.Add(t.Updated);
        return string.Join(" · ", parts);
    }

    /// <summary>A language's name in itself ("vi" -> "Tiếng Việt"), or its code.</summary>
    private static string LanguageName(string code)
    {
        try
        {
            string name = CultureInfo.GetCultureInfo(code).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.GetCultureInfo(code)) + name[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static TextBlock Muted(string text) =>
        new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.InkMuted);

    /// <summary>The game's "…" item: the same choice, after the game was played (its scheme known).</summary>
    private async Task ChangeTranslationAsync(LibraryGame game)
    {
        string? scheme = game.Scheme ?? await Task.Run(() => GameLibrary.SchemeFor(game.Folder)?.Name);
        if (scheme == null)
        {
            Message = Strings.GameNotRecognised;
            return;
        }
        await ChooseTranslationAsync(scheme, game.Folder, play: false);
    }

    // ---- A newer release

    private async Task TellReleaseAsync()
    {
        var release = await AppUpdater.CheckAsync();
        if (release == null || release.Version == PlayerSettings.Load().SkippedRelease)
            return;
        var notes = new TextBlock { Text = Shorter(release.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 13 }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false }.Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink);
        var skip = new Button { Content = Strings.UpdateSkip };
        var later = new Button { Content = Strings.UpdateLater };
        var update = new Button { Content = string.Format(Strings.UpdateNow, Bytes(release.PackageSize)) };
        update.Classes.Add("accent");
        skip.Click += (_, _) =>
        {
            var settings = PlayerSettings.Load();
            settings.SkippedRelease = release.Version;
            settings.Save();
            CloseOverlay();
        };
        later.Click += (_, _) => CloseOverlay();
        update.Click += async (_, _) =>
        {
            update.IsEnabled = later.IsEnabled = skip.IsEnabled = false;
            status.IsVisible = true;
            status.Text = string.Format(Strings.UpdateDownloading, Bytes(0), Bytes(release.PackageSize));
            var progress = new Progress<long>(n => status.Text = string.Format(Strings.UpdateDownloading, Bytes(n), Bytes(release.PackageSize)));
            bool installed;
            try
            {
                installed = await AppUpdater.InstallAsync(release, progress);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                status.Text = string.Format(Strings.UpdateFailed, e.Message);
                update.IsEnabled = later.IsEnabled = skip.IsEnabled = true;
                return;
            }
            if (installed)
            {
                CloseOverlay();
                if (PlayerPlatform.InstallPackage == null)
                    QuitForUpdate?.Invoke();
                return;
            }
            // Android: installing from OpenShiina is to be allowed first (its settings page opened)
            if (PlayerPlatform.InstallPackage != null)
            {
                status.Text = Strings.UpdateAllowInstall;
                update.IsEnabled = later.IsEnabled = skip.IsEnabled = true;
                return;
            }
            // (a folder the player may not write in: the release's page)
            status.Text = Strings.UpdateByHand;
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(release.Page));
            later.IsEnabled = true;
        };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { skip, later, update } };
        foreach (var b in buttons.Children)
            b.Margin = new Thickness(8, 0, 0, 8);
        var dialog = new Border
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(20),
            MaxWidth = 560, Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = string.Format(Strings.UpdateTitle, release.Version), FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }
                        .Use(TextBlock.ForegroundProperty, PlayerTheme.Keys.Ink),
                    Muted(string.Format(Strings.UpdateYouHave, AppVersion.Text)),
                    new ScrollViewer { MaxHeight = 260, Content = notes },
                    status, buttons,
                },
            },
        }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface).Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border);
        ShowOverlay(dialog);
    }

    /// <summary>A release's notes for the dialog: the Markdown's marks left out, at most 30 lines.</summary>
    private static string Shorter(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Replace("**", "").Replace("`", "").TrimEnd())
            .Select(l => l.StartsWith("## ", StringComparison.Ordinal) ? l[3..] : l.StartsWith("- ", StringComparison.Ordinal) ? "• " + l[2..] : l)
            .ToList();
        return string.Join('\n', lines.Take(30)) + (lines.Count > 30 ? "\n…" : "");
    }
}
