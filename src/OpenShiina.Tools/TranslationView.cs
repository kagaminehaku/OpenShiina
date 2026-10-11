// The translation tab: a game chosen, its translation's tables (Core Game/ScenarioText.cs) made in
// the game folder's translation\ the first time and edited here: the scenarios a file at a time
// (or every line at once), the speakers' names, and the strings the scripts write themselves. The
// tables are saved as they change; the player reads them as they are, so "Play" shows the
// translation at once. The tables stay plain text, to go through a spreadsheet or a translation
// tool as well.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenShiina.Archives;
using OpenShiina.Game;
using OpenShiina.Scripting;

namespace OpenShiina.Tools;

public sealed class TranslationView : UserControl
{
    private const string AllLines = "*all", Names = "*names", Strings = "*strings";

    private readonly Button m_choose = new() { Content = "Translate a game…" };
    private readonly TextBlock m_game = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Text = "Choose the folder of an installed game (the one with its .exe and .WAR files)." };
    private readonly Button m_play = new() { Content = "▶ Play", IsEnabled = false };
    private readonly Button m_check = new() { Content = "Check", IsEnabled = false };
    private readonly Button m_update = new() { Content = "Update from the game", IsEnabled = false };
    private readonly ComboBox m_schemes = new() { MinWidth = 260, MaxDropDownHeight = 500 };
    private readonly ListBox m_files = new();
    private readonly TextBox m_search = new() { PlaceholderText = "Search the original or the translation", MinWidth = 280 };
    private readonly CheckBox m_untranslated = new() { Content = "Untranslated only" };
    private readonly Button m_addString = new() { Content = "Add a string", IsVisible = false };
    private readonly ListBox m_rows = new();
    private readonly TextBlock m_status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button m_problems = new() { IsVisible = false };
    private readonly DispatcherTimer m_saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private string? m_folder, m_tablesFolder;
    private EncryptionScheme? m_scheme;
    private List<(string Name, byte[] Data)> m_scenarios = new();
    private TranslationTables? m_tables;
    private bool m_dirty;
    private List<string> m_lastProblems = new();
    private static string? s_player;

    private sealed record FileItem(string Key, string Label);

    /// <summary>The game's scenarios are read or the tables made.</summary>
    public bool Busy { get; private set; }

    public TranslationView()
    {
        m_schemes.ItemsSource = Ui.SchemeNames();
        m_schemes.SelectedIndex = 0;
        m_files.ItemTemplate = new FuncDataTemplate<FileItem>((item, _) => new TextBlock { Text = item?.Label ?? "", TextTrimming = TextTrimming.CharacterEllipsis });
        m_files.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 3)), new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        m_rows.ItemTemplate = new FuncDataTemplate<object>((row, _) => row switch
        {
            TextRow line => LineView(line),
            StringRow text => StringView(text),
            _ => new TextBlock(),
        });

        var advanced = new Expander
        {
            Header = "Scheme",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Auto finds the game by its .exe, else by its keys. Choose the game here only when Auto does not.", TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Opacity = 0.8 },
                    m_schemes,
                },
            },
        };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { m_update, m_check, m_play } };
        DockPanel.SetDock(m_choose, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        DockPanel.SetDock(advanced, Dock.Right);
        m_game.Margin = new Thickness(12, 0);
        top.Children.Add(m_choose);
        top.Children.Add(right);
        top.Children.Add(advanced);
        top.Children.Add(m_game);
        advanced.Margin = new Thickness(0, 0, 8, 0);

        var keys = new TextBlock { Text = "Enter: the next line   Shift+Enter: the line before", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        var filter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6), Children = { m_search, m_untranslated, m_addString, keys } };
        var editor = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(filter, Dock.Top);
        editor.Children.Add(filter);
        editor.Children.Add(new Border { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1), Child = m_rows });

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("240,Auto,*") };
        lists.Children.Add(new Border { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1), Child = m_files });
        var splitter = new GridSplitter { Width = 6, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(editor, 2);
        lists.Children.Add(splitter);
        lists.Children.Add(editor);

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(m_problems, Dock.Right);
        bottom.Children.Add(m_problems);
        bottom.Children.Add(m_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(lists);
        Content = root;

        ToolTip.SetTip(m_play, "Save, then play the game in OpenShiina with the translation as it is now.");
        ToolTip.SetTip(m_check, "What the player will make of the translation: the codes its letters take (2,820 in all), and the lines that do not fit the game's.");
        ToolTip.SetTip(m_update, "Read the game's scenarios again and bring the tables up to date (after an update of the game): translations of lines that did not change are kept.");
        ToolTip.SetTip(m_addString, "A text the scripts write themselves (a choice, a menu): exactly as the game shows it, and its translation.");

        m_choose.Click += async (_, _) =>
        {
            if (await Ui.PickFolder(this, "The folder of the game to translate", m_folder) is { } folder)
                await Open(folder, update: false);
        };
        m_files.SelectionChanged += (_, _) => ShowRows();
        m_search.TextChanged += (_, _) => ShowRows();
        m_untranslated.IsCheckedChanged += (_, _) => ShowRows();
        m_addString.Click += (_, _) =>
        {
            if (m_tables == null)
                return;
            var row = new StringRow { Original = "" };
            m_tables.Strings.Add(row);
            ShowRows();
            m_rows.ScrollIntoView(row);
        };
        m_saveTimer.Tick += (_, _) => Save();
        m_play.Click += (_, _) => Play();
        m_check.Click += (_, _) => Check(show: true);
        m_update.Click += async (_, _) =>
        {
            if (m_folder != null)
                await Open(m_folder, update: true);
        };
        m_problems.Click += async (_, _) =>
            await Ui.Message(this, string.Join("\n", m_lastProblems.Take(40)) + (m_lastProblems.Count > 40 ? $"\n… and {m_lastProblems.Count - 40} more" : ""), "Lines that do not fit the game");
    }

    /// <summary>Opens the translation of the game in <paramref name="folder"/>.</summary>
    public async void Open(string folder) => await Open(folder, update: false);

    /// <summary>Opens a game's translation: its tables made the first time (or brought up to date).</summary>
    private async Task Open(string folder, bool update)
    {
        if (Busy)
            return;
        Save();
        Busy = true;
        SetEnabled(false);
        var chosen = Ui.Chosen(m_schemes);
        try
        {
            m_status.Text = "Reading the game's scenarios…";
            var scheme = chosen ?? await Task.Run(() => FindScheme(folder));
            if (scheme == null)
            {
                await Ui.Message(this, "The game was not recognised: no known .exe is in its folder, and no scheme's keys decode its archives. Choose the game under Scheme, then open it again.");
                m_status.Text = "";
                return;
            }
            var scenarios = await Task.Run(() => ScenarioText.ReadScenarios(folder, scheme));
            if (scenarios.Count == 0)
            {
                await Ui.Message(this, "No scenario (.TXT) was found in the game's archives.");
                m_status.Text = "";
                return;
            }
            string tables = Path.Combine(folder, TranslationTables.Folder);
            string note = "";
            if (update || !TranslationTables.Exist(tables))
            {
                var made = await Task.Run(() => ScenarioText.Export(scenarios, tables));
                note = update
                    ? $"Brought up to date: {made.Lines:N0} lines, {made.Kept:N0} translations kept."
                    : $"Made the tables: {made.Lines:N0} lines of {made.Files} scenarios, {made.Names} names.";
            }
            m_folder = folder;
            m_tablesFolder = tables;
            m_scheme = scheme;
            m_scenarios = scenarios;
            m_tables = TranslationTables.ReadFolder(tables);
            m_dirty = false;
            m_game.Text = $"{scheme.Name}  —  {folder}";
            Refresh();
            m_files.SelectedIndex = m_files.ItemCount > 3 ? 3 : 0;
            m_status.Text = note;
            // Scenario files of their own (hand-made, or an older build's) come before text.tsv
            var own = Directory.GetFiles(tables, "*.txt", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Select(Path.GetFileName).Where(n => !n!.Equals("strings.txt", StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Count > 0)
                await Ui.Message(this, $"The translation folder also has {own.Count} scenario files of its own ({string.Join(", ", own.Take(5))}{(own.Count > 5 ? ", …" : "")}). The player reads such a file in place of the lines of text.tsv for that scenario: remove it to see the table's translation.");
            Check(show: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Ui.Message(this, $"The translation could not be made in {Path.Combine(folder, TranslationTables.Folder)}: {ex.Message}\n\nThe game's folder must be one this account may write to.");
            m_status.Text = "";
        }
        finally
        {
            Busy = false;
            SetEnabled(true);
        }
    }

    private void SetEnabled(bool enabled)
    {
        m_choose.IsEnabled = enabled;
        m_play.IsEnabled = m_check.IsEnabled = m_update.IsEnabled = enabled && m_tables != null;
    }

    /// <summary>The game's scheme: by its .exe (or the player's library), else the first its keys fit.</summary>
    private static EncryptionScheme? FindScheme(string folder)
    {
        if (GameLibrary.SchemeFor(folder) is { } scheme)
            return scheme;
        foreach (var candidate in SchemeDetector.Detect(folder))
            if (FormatManager.Instance.GetScheme(candidate.Name) is { } found)
                return found;
        return null;
    }

    /// <summary>The list of files with how far each is translated (the selection kept).</summary>
    private void Refresh()
    {
        if (m_tables is not { } tables)
            return;
        string? selected = (m_files.SelectedItem as FileItem)?.Key;
        int done = tables.Lines.Count(l => l.Translation.Trim().Length > 0);
        var items = new List<FileItem>
        {
            new(AllLines, $"All lines   {done:N0}/{tables.Lines.Count:N0}"),
            new(Names, $"Names   {tables.Names.Count(n => n.Translation.Trim().Length > 0)}/{tables.Names.Count}"),
            new(Strings, $"Strings   {tables.Strings.Count}"),
        };
        foreach (var file in tables.Lines.GroupBy(l => l.File, StringComparer.OrdinalIgnoreCase))
            items.Add(new FileItem(file.Key, $"{file.Key}   {file.Count(l => l.Translation.Trim().Length > 0)}/{file.Count()}"));
        m_files.ItemsSource = items;
        if (selected != null && items.FindIndex(i => i.Key == selected) is >= 0 and int index)
            m_files.SelectedIndex = index;
    }

    /// <summary>"6 of 23,450 lines (under 0.1%)".</summary>
    private static string Percent(int done, int all)
    {
        double share = all == 0 ? 0 : done * 100.0 / all;
        string percent = done == 0 ? "0" : share < 0.1 ? "under 0.1" : share.ToString("0.#");
        return $"{done:N0} of {all:N0} lines ({percent}%)";
    }

    private void ShowRows()
    {
        if (m_tables is not { } tables || m_files.SelectedItem is not FileItem file)
        {
            m_rows.ItemsSource = null;
            return;
        }
        string search = m_search.Text?.Trim() ?? "";
        bool untranslated = m_untranslated.IsChecked == true;
        bool Shows(string original, string translation) =>
            (!untranslated || translation.Trim().Length == 0)
            && (search.Length == 0 || original.Contains(search, StringComparison.OrdinalIgnoreCase) || translation.Contains(search, StringComparison.OrdinalIgnoreCase));
        m_addString.IsVisible = file.Key == Strings;
        m_rows.ItemsSource = file.Key switch
        {
            Names => tables.Names.Where(n => Shows(n.Original, n.Translation)).Cast<object>().ToList(),
            Strings => tables.Strings.Where(s => s.Original.Length == 0 || Shows(s.Original, s.Translation)).Cast<object>().ToList(),
            AllLines => tables.Lines.Where(l => Shows(l.Original, l.Translation)).Cast<object>().ToList(),
            _ => tables.Lines.Where(l => l.File.Equals(file.Key, StringComparison.OrdinalIgnoreCase) && Shows(l.Original, l.Translation)).Cast<object>().ToList(),
        };
    }

    private Control LineView(TextRow row)
    {
        var heading = new TextBlock
        {
            Text = row.Speaker.Length > 0 ? $"{row.Speaker}   {row.Id}" : row.Id,
            FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 2),
        };
        var original = new SelectableTextBlock { Text = row.Original, TextWrapping = TextWrapping.Wrap };
        var translation = new TextBox { Text = row.Translation, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Translation" };
        translation.TextChanged += (_, _) =>
        {
            if (translation.Text != row.Translation)
            {
                row.Translation = translation.Text ?? "";
                Changed();
            }
        };
        translation.KeyDown += (_, e) => MoveOn(row, e);
        return Columns(new StackPanel { Children = { heading, original } }, translation);
    }

    private Control StringView(StringRow row)
    {
        Control original;
        if (m_tables?.Strings.Contains(row) == true)
        {
            // A string is written by hand: the original too, and it may be taken out
            var box = new TextBox { Text = row.Original, TextWrapping = TextWrapping.Wrap, PlaceholderText = "The text exactly as the game shows it" };
            box.TextChanged += (_, _) =>
            {
                if (box.Text != row.Original)
                {
                    row.Original = box.Text ?? "";
                    Changed();
                }
            };
            var remove = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Top };
            remove.Click += (_, _) =>
            {
                m_tables.Strings.Remove(row);
                Changed();
                ShowRows();
            };
            var line = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            remove.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(remove);
            line.Children.Add(box);
            original = line;
        }
        else
            original = new SelectableTextBlock { Text = row.Original, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var translation = new TextBox { Text = row.Translation, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Translation" };
        translation.TextChanged += (_, _) =>
        {
            if (translation.Text != row.Translation)
            {
                row.Translation = translation.Text ?? "";
                Changed();
            }
        };
        translation.KeyDown += (_, e) => MoveOn(row, e);
        return Columns(original, translation);
    }

    /// <summary>Enter: the translation of the next row; Shift+Enter: of the row before.</summary>
    private void MoveOn(object row, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || m_rows.ItemsSource is not IList<object> rows)
            return;
        e.Handled = true;
        int index = rows.IndexOf(row) + ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
        if (index < 0 || index >= rows.Count)
            return;
        m_rows.ScrollIntoView(index);
        // Once the row's view is made: its translation box (the last one of the row)
        Dispatcher.UIThread.Post(() =>
        {
            if (m_rows.ContainerFromIndex(index)?.GetVisualDescendants().OfType<TextBox>().LastOrDefault() is { } box)
            {
                box.Focus();
                box.CaretIndex = box.Text?.Length ?? 0;
            }
        }, DispatcherPriority.Loaded);
    }

    private static Control Columns(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 2) };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A translation changed: saved a moment later.</summary>
    private void Changed()
    {
        m_dirty = true;
        m_status.Text = "Editing…";
        m_saveTimer.Stop();
        m_saveTimer.Start();
    }

    /// <summary>Writes the tables when something changed (also when the window closes).</summary>
    public void Save()
    {
        m_saveTimer.Stop();
        if (!m_dirty || m_tables == null || m_tablesFolder == null)
            return;
        try
        {
            m_tables.Write(m_tablesFolder);
            m_dirty = false;
            int done = m_tables.Lines.Count(l => l.Translation.Trim().Length > 0);
            m_status.Text = $"Saved. Translated: {Percent(done, m_tables.Lines.Count)}.";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Excel keeps a table it has open locked: try again on the next change
            m_status.Text = $"Not saved: {ex.Message} (is a table open in another program?)";
        }
    }

    /// <summary>What the player will make of the translation, in the status line (and a message).</summary>
    private async void Check(bool show)
    {
        if (m_tables == null || m_tablesFolder == null)
            return;
        Save();
        var scenarios = m_scenarios;
        // A copy to read on another thread while the rows may change here
        var copy = TranslationTables.ReadFolder(m_tablesFolder);
        var summary = await Task.Run(() => copy.Check(scenarios));
        m_lastProblems = summary.Problems;
        m_problems.Content = $"{summary.Problems.Count} lines do not fit";
        m_problems.IsVisible = summary.Problems.Count > 0;
        string codes = $"the letters use {summary.CodesUsed:N0} of {ScnGaiji.Capacity:N0} codes"
                       + (summary.Missing > 0 ? $" ({summary.Missing:N0} found none left and show as \"?\")" : "");
        m_status.Text = $"Translated: {Percent(summary.Translated, summary.Lines)} in {summary.Files} scenarios, {summary.Strings} names and strings; {codes}.";
        if (show)
            await Ui.Message(this, m_status.Text + (summary.Problems.Count > 0 ? $"\n\n{summary.Problems.Count} lines of the tables do not fit the game's (see the button below the list)." : ""));
    }

    /// <summary>Saves, then plays the game in OpenShiina (found next to the tools, else asked for once).</summary>
    private async void Play()
    {
        if (m_folder == null)
            return;
        Save();
        s_player ??= FindPlayer() ?? await Ui.PickFile(this, "Where is OpenShiina (the player)?", ("OpenShiina", [OperatingSystem.IsWindows() ? "OpenShiina.exe" : "OpenShiina"]));
        if (s_player == null)
            return;
        try
        {
            var start = new ProcessStartInfo(s_player) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(s_player)! };
            start.ArgumentList.Add(m_folder);
            Process.Start(start);
        }
        catch (Exception ex)
        {
            s_player = null;
            await Ui.Message(this, $"OpenShiina could not be started: {ex.Message}");
        }
    }

    /// <summary>The player next to the tools: the same folder, a folder beside it (the releases' and the builds' layouts).</summary>
    private static string? FindPlayer()
    {
        string name = OperatingSystem.IsWindows() ? "OpenShiina.exe" : "OpenShiina";
        string here = AppContext.BaseDirectory;
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(here));
        var candidates = new List<string> { Path.Combine(here, name) };
        if (parent != null)
            foreach (var folder in new[] { "OpenShiina", "OpenShiina.Desktop" })
                candidates.Add(Path.Combine(parent, folder, name));
        return candidates.FirstOrDefault(File.Exists);
    }
}
