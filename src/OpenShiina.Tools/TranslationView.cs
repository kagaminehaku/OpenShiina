// The translation tab: a game's scenario text exported into tables to translate (text.tsv,
// names.tsv, strings.tsv), and the tables, translated, built into the translation\ folder the
// player reads in place of the archives' files (Core Game/ScenarioText.cs).

using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenShiina.Archives;
using OpenShiina.Game;
using OpenShiina.Scripting;

namespace OpenShiina.Tools;

public sealed class TranslationView : UserControl
{
    private readonly TextBox m_game = new() { PlaceholderText = "The game's folder (with its .exe and .WAR files)" };
    private readonly TextBox m_tables = new() { PlaceholderText = "Where the tables to translate go" };
    private readonly TextBox m_output = new() { PlaceholderText = "Where translation\\ is written (the tables' folder when empty)" };
    private readonly ComboBox m_schemes = new() { MinWidth = 260, MaxDropDownHeight = 500 };
    private readonly Button m_export = new() { Content = "Export the text" };
    private readonly Button m_build = new() { Content = "Build the translation" };
    private readonly TextBox m_log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, MS Gothic, monospace"),
    };

    /// <summary>An export or build runs.</summary>
    public bool Busy { get; private set; }

    public TranslationView()
    {
        m_schemes.ItemsSource = Ui.SchemeNames();
        m_schemes.SelectedIndex = 0;
        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Text = "1. Export the text: every message line of the game's scenarios goes into text.tsv (id, speaker, original, translation), "
                 + "the speakers' names into names.tsv. Exporting again keeps the translations already in the tables.\n"
                 + "2. Translate: fill the translation column (a spreadsheet, a text editor or a translation tool; keep the tabs and the first three columns). "
                 + "Choices and menus the scripts write themselves go into strings.tsv by hand: the text exactly as the game shows it, a tab, its translation.\n"
                 + "3. Build the translation: the translated scenarios (UTF-8) and strings.txt are written into a folder named translation. "
                 + "Copy that folder into the game's folder: OpenShiina reads it in place of the archives' files, and the game's own files stay as they are.",
        };
        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 12),
        };
        AddRow(form, 0, "Game", m_game, Browse(m_game, "The game's folder"));
        AddRow(form, 1, "Scheme", m_schemes, null);
        AddRow(form, 2, "Tables", m_tables, Browse(m_tables, "The folder for the tables"));
        AddRow(form, 3, "Output", m_output, Browse(m_output, "The folder to write translation\\ into"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12), Children = { m_export, m_build } };
        var top = new StackPanel { Children = { help, form, buttons } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new Border { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1), Child = m_log });
        Content = root;

        m_game.TextChanged += (_, _) =>
        {
            // The tables beside the game by default (never inside it)
            if (string.IsNullOrWhiteSpace(m_tables.Text) && m_game.Text?.Trim() is { Length: > 0 } game
                && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(game)) is { } parent)
                m_tables.PlaceholderText = $"For example {Path.Combine(parent, Path.GetFileName(Path.TrimEndingDirectorySeparator(game)) + " translation")}";
        };
        m_export.Click += (_, _) => Run(export: true);
        m_build.Click += (_, _) => Run(export: false);
    }

    private static void AddRow(Grid grid, int row, string label, Control field, Control? button)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        field.Margin = new Thickness(0, 4);
        if (field is ComboBox)
            field.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        if (button != null)
        {
            button.Margin = new Thickness(8, 4, 0, 4);
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
    }

    private Button Browse(TextBox box, string title)
    {
        var button = new Button { Content = "Browse…" };
        button.Click += async (_, _) =>
        {
            string? near = box.Text?.Trim() is { Length: > 0 } text ? text : m_game.Text?.Trim();
            if (await Ui.PickFolder(this, title, near) is { } folder)
                box.Text = folder;
        };
        return button;
    }

    private void Log(string line) => m_log.Text = (m_log.Text ?? "") + line + "\n";

    private async void Run(bool export)
    {
        if (Busy)
            return;
        string game = m_game.Text?.Trim() ?? "", tables = m_tables.Text?.Trim() ?? "", output = m_output.Text?.Trim() ?? "";
        if (!Directory.Exists(game))
        {
            await Ui.Message(this, "Choose the game's folder first.");
            return;
        }
        if (tables.Length == 0)
        {
            await Ui.Message(this, "Choose a folder for the tables.");
            return;
        }
        if (output.Length == 0)
            output = tables;
        var chosen = Ui.Chosen(m_schemes);
        Busy = true;
        m_export.IsEnabled = m_build.IsEnabled = false;
        m_log.Text = "";
        try
        {
            var scheme = chosen ?? await Task.Run(() => FindScheme(game));
            if (scheme == null)
            {
                Log("The game was not recognised (no known .exe in its folder, and no scheme's keys decode its archives): choose it under Scheme.");
                return;
            }
            Log($"{scheme.Name}: reading the scenarios…");
            var scenarios = await Task.Run(() => ScenarioText.ReadScenarios(game, scheme));
            if (scenarios.Count == 0)
            {
                Log("No scenario (.TXT) was found in the game's archives.");
                return;
            }
            Log($"{scenarios.Count} scenario files.");
            if (export)
            {
                var result = await Task.Run(() => ScenarioText.Export(scenarios, tables));
                Log($"Exported {result.Lines:N0} message lines of {result.Files} files and {result.Names} speakers' names into {tables}:");
                Log($"  {ScenarioText.TextTable}, {ScenarioText.NameTable}, {ScenarioText.StringTable}");
            }
            else
            {
                if (!File.Exists(Path.Combine(tables, ScenarioText.TextTable)))
                {
                    Log($"There is no {ScenarioText.TextTable} in {tables}: export the text first.");
                    return;
                }
                var result = await Task.Run(() => ScenarioText.Build(scenarios, tables, output));
                string target = Path.Combine(output, ScenarioText.TranslationFolder);
                Log($"Built {result.Lines:N0} translated lines in {result.Files} files and {result.Strings} names and strings into {target}.");
                Log($"Codes for the letters: {result.CodesUsed:N0} of {ScnGaiji.Capacity:N0} used"
                    + (result.Missing > 0 ? $"; {result.Missing:N0} letters found none left and will show as \"?\"" : "."));
                if (result.Problems.Count > 0)
                {
                    Log($"\n{result.Problems.Count} problems:");
                    var lines = new StringBuilder();
                    foreach (var problem in result.Problems.Take(200))
                        lines.Append("  ").Append(problem).Append('\n');
                    if (result.Problems.Count > 200)
                        lines.Append($"  … and {result.Problems.Count - 200} more\n");
                    Log(lines.ToString().TrimEnd());
                }
                Log($"\nCopy {target} into the game's folder to play the translation in OpenShiina.");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed: {ex.Message}");
        }
        finally
        {
            Busy = false;
            m_export.IsEnabled = m_build.IsEnabled = true;
        }
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
}
