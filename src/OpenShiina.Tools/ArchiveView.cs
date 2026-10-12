// The archive tab (GrandCrossExtractor's window on Avalonia): a .WAR archive opened with the
// game's scheme (its .exe next to it, else its keys, or the one chosen), its entries listed and
// filtered, previewed (S25 frames, MI4, PNG/JPEG/BMP, the sounds played, scripts as text, else
// hex), extracted as they are or converted (S25 / MI4 -> PNG, the layers of an S25 composed as
// the scenario scripts show them, OGV -> OGG, PAD -> WAV), and edited files packed back into a
// new archive (WARC 1.7), checked by reading every packed entry back before it is saved. A
// standalone .S25 file opens as its frames.

using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using OpenShiina.App;
using OpenShiina.Archives;
using OpenShiina.Audio;
using OpenShiina.Formats;
using OpenShiina.Game;
using OpenShiina.IO;
using OpenShiina.Scripting;

namespace OpenShiina.Tools;

public sealed class ArchiveView : UserControl
{
    private readonly ComboBox m_schemes = new() { MinWidth = 260, MaxDropDownHeight = 500 };
    private readonly TextBox m_search = new() { PlaceholderText = "Search by name", Width = 220 };
    private readonly ComboBox m_filter = new() { ItemsSource = new[] { "All", "Images", "Sounds", "Scripts and text" }, SelectedIndex = 0, Width = 150 };
    private readonly ListBox m_list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock m_name = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Text = "No file selected", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock m_details = new() { Opacity = 0.75, Text = "Open a game's .WAR archive (or an .S25 picture) to see its files." };
    private readonly Image m_image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBox m_text = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, MS Gothic, monospace"),
    };
    private readonly Button m_previous = new() { Content = "◀" }, m_next = new() { Content = "▶" };
    private readonly TextBlock m_frame = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel m_frames;
    private readonly Button m_play = new() { Content = "▶ Play" }, m_stop = new() { Content = "■ Stop" };
    private readonly StackPanel m_sound;
    private readonly CheckBox m_convert = new() { Content = "Convert on extract", IsChecked = true };
    private readonly CheckBox m_compose = new() { Content = "Compose layers", IsChecked = false };
    private readonly Button m_open = new() { Content = "Open archive…" };
    private readonly Button m_extractSelected = new() { Content = "Extract selected…", IsEnabled = false };
    private readonly Button m_extractAll = new() { Content = "Extract all…", IsEnabled = false };
    private readonly Button m_repack = new() { Content = "Repack…", IsEnabled = false };
    private readonly ProgressBar m_progress = new() { Width = 200, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock m_status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    private WarcArchive? m_archive;
    private string? m_path, m_lastTried;
    private List<Entry> m_entries = new();
    // The frames of a standalone .S25 file (no archive): an entry's Offset is its frame
    private List<S25Frame>? m_standalone;
    private List<S25Frame>? m_shownFrames;
    private int m_shownFrame;
    private byte[]? m_shownSound;
    private int m_previewNumber;
    private bool m_reopening;

    // Sounds previewed: the player's mixer on SDL3, made on the first play
    private ScnMixer? m_mixer;
    private IDisposable? m_output;
    private ScnSound? m_sounds;
    private int m_playing;

    /// <summary>An extraction or repack runs (the archive must stay open).</summary>
    public bool Busy { get; private set; }

    public ArchiveView()
    {
        m_schemes.ItemsSource = Ui.SchemeNames();
        m_schemes.SelectedIndex = 0;
        // Thousands of entries: rows as tight as a file list's
        m_list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 3)), new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        m_list.ItemTemplate = new FuncDataTemplate<Entry>((entry, _) => entry == null ? new TextBlock() : new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,70,80"),
            Children =
            {
                new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                Cell(new TextBlock { Text = entry.Type, Opacity = 0.7 }, 1),
                Cell(new TextBlock { Text = Ui.Size(entry.UnpackedSize), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right }, 2),
            },
        });

        m_frames = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = { m_previous, m_frame, m_next } };
        m_sound = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = { m_play, m_stop } };
        var preview = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8), Children = { m_name, m_details, m_frames, m_sound } };
        DockPanel.SetDock(heading, Dock.Top);
        preview.Children.Add(heading);
        preview.Children.Add(new Panel { Children = { new Border { Child = m_image }.Use(Border.BackgroundProperty, PlayerTheme.Keys.Surface), m_text } });
        m_image.IsVisible = m_text.IsVisible = false;

        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,Auto,3*") };
        lists.Children.Add(new Border { BorderThickness = new Thickness(1), Child = m_list }.Use(Border.BorderBrushProperty, PlayerTheme.Keys.Border));
        lists.Children.Add(Cell(new GridSplitter { Width = 6, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns }, 1));
        lists.Children.Add(Cell(preview, 2));

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8),
            Children = { m_open, new TextBlock { Text = "Scheme", VerticalAlignment = VerticalAlignment.Center }, m_schemes, m_search, m_filter },
        };
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
            Children = { m_convert, m_compose, m_extractSelected, m_extractAll, m_repack, m_progress, m_status },
        };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(lists);
        Content = root;
        ToolTip.SetTip(m_compose, "Event CGs and sprites are a base picture and layers (eyes, mouth): save the whole pictures the scenario scripts show (they are read from the archives next to this one).");
        ToolTip.SetTip(m_repack, "Pack the files of a folder named as entries of this archive (extracted with Convert on extract off, then edited) into a new archive; the other entries are copied as they are.");

        m_open.Click += async (_, _) =>
        {
            if (await Ui.PickFile(this, "Open a ShiinaRio archive or S25 picture", ("ShiinaRio archives and pictures", ["*.war", "*.WAR", "*.s25", "*.S25"])) is { } path)
                Open(path);
        };
        m_schemes.SelectionChanged += (_, _) =>
        {
            if (!m_reopening && m_standalone == null && (m_path ?? m_lastTried) is { } path)
                Open(path);
        };
        m_search.TextChanged += (_, _) => Filter();
        m_filter.SelectionChanged += (_, _) => Filter();
        m_list.SelectionChanged += (_, _) => Preview();
        m_list.DoubleTapped += (_, _) =>
        {
            if (m_list.SelectedItem is Entry entry)
                ExtractOne(entry);
        };
        m_previous.Click += (_, _) => ShowFrame(m_shownFrame - 1);
        m_next.Click += (_, _) => ShowFrame(m_shownFrame + 1);
        m_play.Click += (_, _) => Play();
        m_stop.Click += (_, _) => StopSound();
        m_extractSelected.Click += async (_, _) =>
        {
            var selected = m_list.SelectedItems?.OfType<Entry>().ToList() ?? new();
            if (selected.Count > 0 && await Ui.PickFolder(this, "Extract the selected files into", Folder()) is { } folder)
                await Extract(selected, folder);
        };
        m_extractAll.Click += async (_, _) =>
        {
            if (m_entries.Count > 0 && await Ui.PickFolder(this, "Extract every file into", Folder()) is { } folder)
                await Extract(m_entries, folder);
        };
        m_repack.Click += (_, _) => Repack();
    }

    private static Control Cell(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private string? Folder() => m_path != null ? Path.GetDirectoryName(Path.GetFullPath(m_path)) : null;

    /// <summary>Opens an archive (or a standalone .S25) with the scheme chosen.</summary>
    public async void Open(string path)
    {
        if (Busy)
            return;
        Close();
        m_lastTried = path;
        m_status.Text = $"Opening {Path.GetFileName(path)}…";
        var scheme = Ui.Chosen(m_schemes);
        try
        {
            var (warc, found, frames) = await Task.Run(() => OpenFile(path, scheme));
            if (warc != null)
            {
                m_archive = warc;
                m_path = path;
                m_entries = warc.Entries;
                m_status.Text = $"{Path.GetFileName(path)}: {warc.SchemeName}" + (found != null ? $" ({found})" : "");
                if (found != null && scheme == null)
                {
                    // Show the scheme the keys gave, without opening the archive again
                    m_reopening = true;
                    m_schemes.SelectedItem = warc.SchemeName;
                    m_reopening = false;
                }
            }
            else if (frames != null)
            {
                m_standalone = frames;
                m_path = path;
                string stem = Path.GetFileNameWithoutExtension(path);
                m_entries = frames.Select((f, i) => new Entry
                {
                    Name = $"{stem}@{f.Index:D4}.png", Type = "image", Offset = i,
                    UnpackedSize = (uint)(f.Width * f.Height * 4),
                }).ToList();
                m_status.Text = $"{Path.GetFileName(path)}: an S25 picture, {frames.Count} frames";
            }
            else
            {
                m_status.Text = "";
                await Ui.Message(this, found ?? $"{Path.GetFileName(path)} is not a ShiinaRio .WAR archive or .S25 picture.");
                return;
            }
        }
        catch (Exception ex)
        {
            m_status.Text = "";
            await Ui.Message(this, $"Could not open {Path.GetFileName(path)}:\n{ex.Message}");
            return;
        }
        m_extractAll.IsEnabled = m_extractSelected.IsEnabled = true;
        m_repack.IsEnabled = m_archive != null;
        Filter();
    }

    /// <summary>
    /// The archive opened with <paramref name="scheme"/>, or with Auto by the game's .exe next to
    /// it, else by the keys of the folder's archives (SchemeDetector): (archive, how its scheme
    /// was found or why it did not open, the frames of a standalone .S25).
    /// </summary>
    private static (WarcArchive? Archive, string? Found, List<S25Frame>? Frames) OpenFile(string path, EncryptionScheme? scheme)
    {
        var view = new ArcView(path);
        try
        {
            if (WarcOpener.TryOpen(view, scheme) is { } warc)
                return (warc, null, null);
            if (!WarcOpener.IsWarc(view))
            {
                byte[] head = view.View.ReadBytes(0, (uint)Math.Min(8, view.MaxOffset));
                if (!S25Decoder.IsS25(head))
                    return (null, null, null);
                var frames = S25Decoder.DecodeAllFrames(view.View.ReadBytes(0, (uint)view.MaxOffset));
                return (null, null, frames.Count > 0 ? frames : null);
            }
            if (scheme == null && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
            {
                var candidates = SchemeDetector.Detect(folder);
                foreach (var candidate in candidates)
                    if (FormatManager.Instance.GetScheme(candidate.Name) is { } found && WarcOpener.TryOpen(view, found) is { } byKeys)
                        return (byKeys, candidates.Count > 1
                            ? $"found by its keys; {candidates.Count} schemes fit, choose another if files come out wrong"
                            : "found by its keys", null);
            }
            view.Dispose();
            return (null, scheme == null
                ? "The game was not recognised: no known .exe is next to the archive and no scheme's keys decode its files. Choose the game under Scheme."
                : $"The archive does not open with the {scheme.Name} scheme. Choose another under Scheme.", null);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    /// <summary>Closes the archive (the window closes, or another is opened).</summary>
    public void Close()
    {
        StopSound();
        m_archive?.Dispose();
        m_archive = null;
        m_standalone = null;
        m_path = null;
        m_entries = new();
        m_list.ItemsSource = null;
        m_extractAll.IsEnabled = m_extractSelected.IsEnabled = m_repack.IsEnabled = false;
        ClearPreview();
        if (m_output != null)
        {
            m_output.Dispose();
            m_sounds?.Dispose();
            m_mixer?.Dispose();
            m_output = null;
            m_sounds = null;
            m_mixer = null;
        }
    }

    private void Filter()
    {
        string search = m_search.Text?.Trim() ?? "";
        string? type = m_filter.SelectedIndex switch { 1 => "image", 2 => "audio", 3 => "text", _ => null };
        var shown = m_entries.Where(e => (search.Length == 0 || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                                         && (type == null || e.Type == type)).ToList();
        m_list.ItemsSource = shown;
        if (m_entries.Count > 0)
            m_details.Text = $"{shown.Count} of {m_entries.Count} files, {Ui.Size(m_entries.Sum(e => (long)e.UnpackedSize))} in all";
    }

    private void ClearPreview()
    {
        StopSound();
        m_name.Text = "No file selected";
        m_image.Source = null;
        m_image.IsVisible = m_text.IsVisible = m_frames.IsVisible = m_sound.IsVisible = false;
        m_text.Text = "";
        m_shownFrames = null;
        m_shownSound = null;
    }

    // What an entry shows: frames, a picture, a sound, text or a hex dump
    private sealed record Shown(List<S25Frame>? Frames, PixelImage? Picture, Bitmap? Decoded, byte[]? Sound, string? SoundKind, string? Text, string Details);

    private async void Preview()
    {
        int number = ++m_previewNumber;
        if (m_list.SelectedItem is not Entry entry || m_list.SelectedItems?.Count != 1)
        {
            ClearPreview();
            return;
        }
        var archive = m_archive;
        var standalone = m_standalone;
        Shown shown;
        try
        {
            shown = await Task.Run(() => Show(archive, standalone, entry));
        }
        catch (Exception ex)
        {
            shown = new Shown(null, null, null, null, null, null, $"Preview error: {ex.Message}");
        }
        if (number != m_previewNumber)
            return;
        ClearPreview();
        m_name.Text = entry.Name;
        m_details.Text = $"{Ui.Size(entry.UnpackedSize)}, {entry.Type}" + (shown.Details.Length > 0 ? " | " + shown.Details : "");
        if (shown.Frames != null)
        {
            m_shownFrames = shown.Frames;
            m_frames.IsVisible = shown.Frames.Count > 1;
            ShowFrame(standalone != null ? (int)entry.Offset : 0);
        }
        else if (shown.Picture != null || shown.Decoded != null)
        {
            m_image.Source = shown.Decoded ?? ToBitmap(shown.Picture!);
            m_image.IsVisible = true;
        }
        else if (shown.Sound != null)
        {
            m_shownSound = shown.Sound;
            m_sound.IsVisible = true;
            m_details.Text += " | " + shown.SoundKind;
        }
        else if (shown.Text != null)
        {
            m_text.Text = shown.Text;
            m_text.IsVisible = true;
        }
    }

    private const int TextShown = 1 << 20;

    private static Shown Show(WarcArchive? archive, List<S25Frame>? standalone, Entry entry)
    {
        if (standalone != null)
            return new Shown(standalone, null, null, null, null, null, "");
        if (archive == null)
            return new Shown(null, null, null, null, null, null, "");
        byte[] data;
        lock (archive)
            data = WarcOpener.OpenEntry(archive, entry);
        if (S25Decoder.IsS25(data) && S25Decoder.DecodeAllFrames(data) is { Count: > 0 } frames)
            return new Shown(frames, null, null, null, null, null, "");
        if (Mi4Decoder.IsMi4(data) && Mi4Decoder.Decode(data) is { } mi4)
            return new Shown(null, mi4, null, null, null, null, $"{mi4.Width} x {mi4.Height} (MI4)");
        if (entry.Type == "image")
        {
            try
            {
                var bitmap = new Bitmap(new MemoryStream(data));
                return new Shown(null, null, bitmap, null, null, null, $"{bitmap.PixelSize.Width} x {bitmap.PixelSize.Height}");
            }
            catch (Exception)
            {
                // Shown as bytes below
            }
        }
        string? sound = AudioDecoder.IsOgv(data) ? "ShiinaRio OGV (Ogg Vorbis)" : AudioDecoder.IsPad(data) ? "ShiinaRio PAD"
            : data.AsSpan().StartsWith("OggS"u8) ? "Ogg Vorbis" : data.AsSpan().StartsWith("RIFF"u8) ? "WAVE" : null;
        if (sound != null)
            return new Shown(null, null, null, data, sound, null, "");
        if (entry.Type == "text" || LooksLikeText(data))
        {
            string text = ScnGaiji.IsUtf8(data) ? Encoding.UTF8.GetString(data, 3, Math.Min(data.Length, TextShown) - 3)
                : Encodings.cp932.GetString(data, 0, Math.Min(data.Length, TextShown));
            return new Shown(null, null, null, null, null, data.Length > TextShown ? text + "\n…" : text, "");
        }
        return new Shown(null, null, null, null, null, HexDump(data, 4096), "shown as bytes");
    }

    private void ShowFrame(int index)
    {
        if (m_shownFrames is not { } frames || index < 0 || index >= frames.Count)
            return;
        m_shownFrame = index;
        var frame = frames[index];
        m_image.Source = ToBitmap(frame.Image);
        m_image.IsVisible = true;
        m_frame.Text = $"Frame {index + 1} of {frames.Count} (slot {frame.Index}), {frame.Width} x {frame.Height} at ({frame.OffsetX}, {frame.OffsetY})";
    }

    /// <summary>A picture as an Avalonia bitmap (straight alpha BGRA).</summary>
    private static Bitmap ToBitmap(PixelImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var target = bitmap.Lock();
        var row = new byte[image.Width * 4];
        for (int y = 0; y < image.Height; y++)
        {
            int from = y * image.Stride;
            if (image.Layout == PixelLayout.Bgra32)
                Array.Copy(image.Pixels, from, row, 0, row.Length);
            else
                for (int x = 0; x < image.Width; x++)
                {
                    row[x * 4] = image.Pixels[from + x * 3];
                    row[x * 4 + 1] = image.Pixels[from + x * 3 + 1];
                    row[x * 4 + 2] = image.Pixels[from + x * 3 + 2];
                    row[x * 4 + 3] = 0xFF;
                }
            Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
        }
        return bitmap;
    }

    private static bool LooksLikeText(byte[] data)
    {
        int n = Math.Min(data.Length, 512), printable = 0;
        for (int i = 0; i < n; i++)
            if (data[i] is 9 or 10 or 13 or >= 32 and <= 126 or >= 128)
                printable++;
        return n == 0 || printable > n * 0.85;
    }

    private static string HexDump(byte[] data, int most)
    {
        int length = Math.Min(data.Length, most);
        var text = new StringBuilder($"The first {length} of {data.Length} bytes\n\n");
        for (int i = 0; i < length; i += 16)
        {
            text.Append($"{i:X8}  ");
            int n = Math.Min(16, length - i);
            for (int j = 0; j < 16; j++)
                text.Append(j < n ? $"{data[i + j]:X2} " : "   ").Append(j == 7 ? " " : "");
            text.Append(' ');
            for (int j = 0; j < n; j++)
                text.Append(data[i + j] is >= 32 and <= 126 ? (char)data[i + j] : '.');
            text.Append('\n');
        }
        return text.ToString();
    }

    private void Play()
    {
        if (m_shownSound is not { } sound)
            return;
        StopSound();
        if (m_output == null)
        {
            m_mixer = new ScnMixer();
            m_output = SdlAudioOutput.TryStart(m_mixer.Output);
            if (m_output == null)
            {
                m_mixer.Dispose();
                m_mixer = null;
                m_status.Text = "No sound device could be opened.";
                return;
            }
            m_sounds = new ScnSound(m_mixer);
        }
        m_playing = m_sounds!.CreateBuffer(sound);
        m_sounds.Play(m_playing, 0);
    }

    private void StopSound()
    {
        if (m_sounds != null && m_playing != 0)
            m_sounds.Release(m_playing);
        m_playing = 0;
    }

    // The scenario scripts of the open archive's folder, read once a folder and scheme
    private S25ScriptIndex? m_scripts;
    private string? m_scriptsKey;

    /// <summary>The layer combinations the scripts next to the archive show; null when no scripts are there.</summary>
    private async Task<S25ScriptIndex?> Scripts()
    {
        if (m_archive == null || m_path == null)
            return null;
        var scheme = Ui.Chosen(m_schemes) ?? FormatManager.Instance.GetScheme(m_archive.SchemeName);
        string path = m_path, key = $"{Folder()}|{scheme?.Name}";
        if (m_scriptsKey != key)
        {
            m_status.Text = "Reading the scenario scripts…";
            m_scripts = await Task.Run(() => S25ScriptIndex.FromGameFolder(path, scheme));
            m_scriptsKey = key;
        }
        return m_scripts is { IsEmpty: false } ? m_scripts : null;
    }

    /// <summary>The files to write for an entry (made one at a time): as it is, or converted.</summary>
    private static IEnumerable<(string Name, byte[] Data)> FilesOf(WarcArchive? archive, List<S25Frame>? standalone, Entry entry, bool convert, S25ScriptIndex? scripts)
    {
        if (standalone != null)
            return [(entry.Name, PngEncoder.Encode(standalone[(int)entry.Offset].Image))];
        if (archive == null)
            throw new InvalidOperationException("No archive is open.");
        byte[] data;
        lock (archive)
            data = WarcOpener.OpenEntry(archive, entry);
        return convert ? MediaConverter.Convert(entry.Name, data, scripts) : [(entry.Name, data)];
    }

    private async void ExtractOne(Entry entry)
    {
        if (Busy)
            return;
        try
        {
            bool convert = m_convert.IsChecked == true;
            var scripts = convert && m_compose.IsChecked == true && entry.Name.EndsWith(".S25", StringComparison.OrdinalIgnoreCase) ? await Scripts() : null;
            m_status.Text = "";
            var files = await Task.Run(() => FilesOf(m_archive, m_standalone, entry, convert, scripts).Take(2).ToList());
            if (files.Count == 0)
                return;
            if (files.Count > 1)
            {
                // Frames or composed pictures: the whole set into a folder
                if (await Ui.PickFolder(this, $"Extract the pictures of {entry.Name} into", Folder()) is { } folder)
                    await Extract([entry], folder);
                return;
            }
            if (await Ui.PickSave(this, "Save the file", MediaConverter.SafeFileName(files[0].Name)) is not { } path)
                return;
            await File.WriteAllBytesAsync(path, files[0].Data);
            m_status.Text = $"Saved {path}";
        }
        catch (Exception ex)
        {
            await Ui.Message(this, $"{entry.Name} could not be extracted: {ex.Message}");
        }
    }

    private void SetBusy(bool busy, int steps = 0)
    {
        Busy = busy;
        m_open.IsEnabled = m_schemes.IsEnabled = !busy;
        m_extractAll.IsEnabled = m_extractSelected.IsEnabled = !busy && m_entries.Count > 0;
        m_repack.IsEnabled = !busy && m_archive != null;
        m_progress.IsVisible = busy;
        m_progress.Maximum = Math.Max(1, steps);
        m_progress.Value = 0;
    }

    private async Task Extract(List<Entry> entries, string folder)
    {
        if (Busy || (m_archive == null && m_standalone == null))
            return;
        bool convert = m_convert.IsChecked == true;
        S25ScriptIndex? scripts = null;
        if (convert && m_compose.IsChecked == true && m_archive != null && entries.Any(e => e.Name.EndsWith(".S25", StringComparison.OrdinalIgnoreCase)))
        {
            scripts = await Scripts();
            if (scripts == null)
                await Ui.Message(this, "No scenario scripts were found next to this archive (normally the game's *_T.WAR), so which layers the game shows together is not known: layered pictures are saved as their frames.");
        }
        var archive = m_archive;
        var standalone = m_standalone;
        SetBusy(true, entries.Count);
        int written = 0;
        var failures = new List<string>();
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                try
                {
                    foreach (var (name, data) in FilesOf(archive, standalone, entry, convert, scripts))
                    {
                        File.WriteAllBytes(MediaConverter.SafeOutputPath(folder, name), data);
                        written++;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.Name}: {ex.Message}");
                }
                int done = i + 1;
                Dispatcher.UIThread.Post(() =>
                {
                    m_progress.Value = done;
                    m_status.Text = $"Extracting {entry.Name} ({done} of {entries.Count}), {written} files written";
                });
            }
        });
        SetBusy(false);
        m_status.Text = $"Extracted {entries.Count - failures.Count} of {entries.Count} files ({written} written) into {folder}";
        if (failures.Count > 0)
            await Ui.Message(this,
                $"{failures.Count} of {entries.Count} files could not be extracted. If most failed, the archive is probably open with the wrong scheme.\n\n"
                + string.Join("\n", failures.Take(15)) + (failures.Count > 15 ? $"\n… and {failures.Count - 15} more" : ""));
    }

    private async void Repack()
    {
        if (Busy || m_archive is not { } archive || m_path == null)
            return;
        if (archive.Decoder.WarcVersion != 170)
        {
            await Ui.Message(this, "Only WARC 1.7 archives can be packed again (older versions keep their index in a form there is no encoder for).");
            return;
        }
        if (await Ui.PickFolder(this, "The folder with the edited files (extracted with Convert on extract off)", Folder()) is not { } source)
            return;
        if (await Ui.PickSave(this, "Save the new archive as", Path.GetFileName(m_path)) is not { } target)
            return;
        target = Path.GetFullPath(target);
        if (string.Equals(target, Path.GetFullPath(m_path), StringComparison.OrdinalIgnoreCase))
        {
            await Ui.Message(this, "The open archive cannot be written over while it is read. Save the new one elsewhere, then put it in place of the old one (keep a copy of the original).");
            return;
        }
        string temporary = target + ".tmp";
        SetBusy(true, archive.Entries.Count);
        var progress = new Progress<(int Current, int Total, string Name)>(p =>
        {
            m_progress.Value = p.Current;
            m_status.Text = $"Packing {p.Name} ({p.Current} of {p.Total})";
        });
        int replaced = 0;
        var wrong = new List<string>();
        string? error = null;
        try
        {
            replaced = await Task.Run(() => WarcPacker.Repack(archive, source, temporary, progress));
            m_status.Text = "Reading the new archive back…";
            wrong = await Task.Run(() => WarcPacker.Verify(temporary, archive, source));
            if (wrong.Count == 0)
                File.Move(temporary, target, overwrite: true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        SetBusy(false);
        m_status.Text = "";
        if (error != null)
            await Ui.Message(this, $"Packing failed: {error}");
        else if (wrong.Count > 0)
            await Ui.Message(this, $"The new archive was not saved: {wrong.Count} entries did not read back as their files.\n\n" + string.Join("\n", wrong.Take(15)));
        else if (replaced == 0)
            await Ui.Message(this, $"No file in {source} has the name of an entry of this archive, so nothing was packed. Extract with Convert on extract off, edit those files and keep their names.");
        else
        {
            m_status.Text = $"Saved {target}";
            await Ui.Message(this, $"Packed {replaced} edited files; the other {archive.Entries.Count - replaced} entries were copied as they were.\n\nSaved as {target}");
        }
    }
}
