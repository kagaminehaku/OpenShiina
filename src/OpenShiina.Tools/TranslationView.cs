// The translation tab: a game chosen, its translation's tables (Core Game/ScenarioText.cs) made in
// the game folder's translation\ the first time and edited here: the scenarios a file at a time
// (or every line at once), the speakers' names, and the strings the scripts write themselves. The
// tables are saved as they change; the player reads them as they are, so "Play" (the game in a
// window of the tools, the player's GameView) shows the translation at once. The tables stay
// plain text, to go through a spreadsheet or a translation tool as well.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenShiina.App;
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
    // The line the game playing shows ("MAK00.TXT:28"): a click goes to it
    private readonly Button m_inGame = new() { IsVisible = false };
    private string? m_shownLine;
    private readonly Button m_update = new() { IsVisible = false };
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
    // The latest check asked for: an older one finishing after it is not shown
    private int m_checks;
    // The lines where the game and text.tsv differ, as the latest check found
    private int m_outdated;
    // The game "Play" started, in its own window (one at a time), and the folder it plays
    private Window? m_playWindow;
    private GameView? m_playView;
    private string? m_playFolder;

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
        DockPanel.SetDock(m_choose, Dock.Left);
        DockPanel.SetDock(m_play, Dock.Right);
        DockPanel.SetDock(m_inGame, Dock.Right);
        DockPanel.SetDock(advanced, Dock.Right);
        m_game.Margin = new Thickness(12, 0);
        m_inGame.Margin = new Thickness(0, 0, 8, 0);
        top.Children.Add(m_choose);
        top.Children.Add(m_play);
        top.Children.Add(m_inGame);
        top.Children.Add(advanced);
        top.Children.Add(m_game);
        advanced.Margin = new Thickness(0, 0, 8, 0);

        var keys = new TextBlock { Text = "Enter: the next line   Shift+Enter: the line before", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        var filter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 6), Children = { m_search, m_untranslated, m_addString, keys } };
        var editor = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(filter, Dock.Top);
        editor.Children.Add(filter);
        editor.Children.Add(new Border { BorderThickness = new Thickness(1), Child = m_rows }.Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border));

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("240,Auto,*") };
        lists.Children.Add(new Border { BorderThickness = new Thickness(1), Child = m_files }.Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border));
        var splitter = new GridSplitter { Width = 6, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(editor, 2);
        lists.Children.Add(splitter);
        lists.Children.Add(editor);

        // The status line, the translation checked after each save (Check); a button for what
        // needs a look, only when something does
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var needs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { m_problems, m_update } };
        DockPanel.SetDock(needs, Dock.Right);
        bottom.Children.Add(needs);
        bottom.Children.Add(m_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(lists);
        Content = root;

        ToolTip.SetTip(m_play, "Save, then play the game in a window of the tools with the translation as it is now. What you save later reaches it from its next scenario.");
        ToolTip.SetTip(m_inGame, "The line the game shows: go to it in the table.");
        ToolTip.SetTip(m_update, "The game's scenarios are not the table's (after an update of the game): bring the table up to date with them. Translations of lines that did not change are kept.");
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
        m_inGame.Click += (_, _) =>
        {
            if (m_shownLine != null)
                GoTo(m_shownLine);
        };
        m_update.Click += async (_, _) =>
        {
            if (m_folder != null && await Ui.Ask(this, $"The game's scenarios and text.tsv differ in {Count(m_outdated, "line")} (after an update of the game?).\n\n"
                    + "Bring the table up to date with the game? The translations of lines that did not change are kept; lines the game no longer has, "
                    + "or whose original changed, leave the table with their translations (keep a copy of the translation folder to have them)."))
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
                    ? $"Brought up to date: {Count(made.Lines, "line")}, {Count(made.Kept, "translation")} kept."
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
            Check(note);
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
        m_play.IsEnabled = m_update.IsEnabled = enabled && m_tables != null;
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

    /// <summary>"1 line", "2,835 lines".</summary>
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

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
        // Right-click on the original: play the game from this line (Ctrl+C still copies). The
        // text and the list's item take the right button for themselves: the menu opens on its
        // release however they handled it
        var left = new StackPanel { Background = Brushes.Transparent, Children = { heading, original } };
        original.ContextFlyout = null;
        left.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                PlayFromMenu(row).Open(left);
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return Columns(left, translation);
    }

    private ContextMenu PlayFromMenu(TextRow row)
    {
        var play = new MenuItem { Header = "▶ Play from here (experimental)" };
        ToolTip.SetTip(play, "An extra, still being tried: the game opens at this line, the scene set as the story sets it. What earlier scenes and choices set is not there, and some games may go wrong.");
        play.Click += (_, _) => Play(row);
        return new ContextMenu { ItemsSource = new[] { play } };
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
        if (index >= 0 && index < rows.Count)
            FocusRow(index);
    }

    /// <summary>A row in view, its translation box (the last one of the row) focused.</summary>
    private void FocusRow(int index)
    {
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

    /// <summary>
    /// Goes to a line of text.tsv ("MAK00.TXT:28"): its scenario chosen in the list, the row in
    /// view and its translation box focused (the search and "Untranslated only" cleared when they
    /// hide it).
    /// </summary>
    private void GoTo(string id)
    {
        if (m_tables == null || m_files.ItemsSource is not List<FileItem> items)
            return;
        if (m_tables.Lines.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } row)
        {
            m_status.Text = $"{id} is not a line of the table: is it another version of the game?";
            return;
        }
        if (items.FindIndex(i => i.Key.Equals(row.File, StringComparison.OrdinalIgnoreCase)) is >= 0 and int file && m_files.SelectedIndex != file)
            m_files.SelectedIndex = file;
        if (m_rows.ItemsSource is IList<object> shown && !shown.Contains(row))
        {
            m_search.Text = "";
            m_untranslated.IsChecked = false;
            ShowRows();
        }
        if (m_rows.ItemsSource is IList<object> rows && rows.IndexOf(row) is >= 0 and int index)
            FocusRow(index);
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
            // The game playing reads it from its next scenario
            if (m_playFolder == m_folder)
                m_playView?.ReloadTranslation();
            Refresh();
            Check("Saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Excel keeps a table it has open locked: try again on the next change
            m_status.Text = $"Not saved: {ex.Message} (is a table open in another program?)";
        }
    }

    /// <summary>
    /// What the player will make of the translation, in the status line after <paramref name="note"/>:
    /// how far it is and the codes its letters take (2,820 in all); and buttons for the lines that
    /// do not fit the game's and for a game whose scenarios are not the table's.
    /// </summary>
    private async void Check(string note = "")
    {
        if (m_tables == null || m_tablesFolder == null)
            return;
        int check = ++m_checks;
        var scenarios = m_scenarios;
        // A copy to read on another thread while the rows may change here
        var copy = TranslationTables.ReadFolder(m_tablesFolder);
        var summary = await Task.Run(() => copy.Check(scenarios));
        if (check != m_checks)
            return;
        m_lastProblems = summary.Problems;
        m_problems.Content = summary.Problems.Count == 1 ? "1 line does not fit" : $"{summary.Problems.Count:N0} lines do not fit";
        m_problems.IsVisible = summary.Problems.Count > 0;
        m_outdated = summary.Outdated;
        m_update.Content = $"The game differs in {Count(summary.Outdated, "line")}: update…";
        m_update.IsVisible = summary.Outdated > 0;
        // The codes: a warning from nine tenths taken, before letters start to show as "?"
        string codes = $"the letters use {summary.CodesUsed:N0} of {ScnGaiji.Capacity:N0} codes"
                       + (summary.Missing > 0 ? $" ({summary.Missing:N0} found none left and show as \"?\")" : "");
        bool full = summary.Missing > 0 || summary.CodesUsed >= ScnGaiji.Capacity * 9 / 10;
        m_status.Text = (note.Length > 0 ? note + " " : "") + (full ? "⚠ " : "")
                        + $"Translated: {Percent(summary.Translated, summary.Lines)} in {summary.Files} scenarios, {summary.Strings} names and strings; {codes}.";
    }

    /// <summary>
    /// Saves, then plays the game in a window of the tools, as the player would: its title, size
    /// and full screen follow the game, its X asks the game first, and the window goes when the game
    /// ends. The game playing comes to the front instead of a second one.
    /// </summary>
    /// <param name="from">
    /// "Play from here": a new game starts at this line, the scenario put in place of the first
    /// one and what comes before the line taken out (ScnVm.Warp.cs); its saves stay in memory.
    /// </param>
    private async void Play(TextRow? from = null)
    {
        if (m_folder == null || m_scheme == null)
            return;
        Save();
        if (m_playWindow != null)
        {
            if (from == null)
            {
                if (m_playFolder == m_folder)
                    m_playWindow.Activate();
                else
                    await Ui.Message(this, $"{m_playWindow.Title} is playing: close its window first.");
                return;
            }
            if (!await Ui.Ask(this, $"{m_playWindow.Title} is playing. Close it and play from {from.Id}?"))
                return;
            // Closed at once, without asking the game
            var old = m_playWindow;
            m_playView?.Close();
            old.Close();
            if (m_playWindow == old)
                return;
        }
        var view = new GameView(library: false)
        {
            ShowsLineIds = true,
            PlayFrom = from == null || m_tables == null ? null : (from.File, from.Line, m_tables.Lines.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
        };
        view.LineShown += id =>
        {
            m_shownLine = id;
            m_inGame.Content = $"In the game: {id}";
            m_inGame.IsVisible = true;
        };
        var window = new Window
        {
            Title = m_scheme.Name,
            Icon = OpenShiinaApplication.Logo(),
            Content = view,
            Width = 800,
            Height = 600,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        OpenShiinaApplication.HostGame(window, view);
        // After the X the game let close, or its own Quit
        view.GameEnded += () => Dispatcher.UIThread.Post(window.Close);
        window.Closed += (_, _) =>
        {
            if (m_playWindow == window)
            {
                m_inGame.IsVisible = false;
                m_shownLine = null;
                m_playWindow = null;
                m_playView = null;
                m_playFolder = null;
            }
        };
        m_playWindow = window;
        m_playView = view;
        m_playFolder = m_folder;
        window.Show();
        if (await view.OpenAsync(m_folder, m_scheme) is { } error)
        {
            window.Close();
            await Ui.Message(this, error);
        }
    }

    /// <summary>The tools close: the game playing is asked to close too (it may ask first, as on its X).</summary>
    public void CloseGame() => m_playWindow?.Close();
}
