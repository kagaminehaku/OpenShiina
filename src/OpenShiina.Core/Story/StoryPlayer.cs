// The story engine: runs a game's flow script SRC_MAIN.SCN and its scenario commands on an
// IStage, with the sound, the auto / skip modes, the backlog, settings and saves. The front end
// shows the message window and the choices (IStoryView) and passes the clicks on. Command
// meanings come from START.SCN and EFCLIB.SCN; see docs/engine-notes.md, section 8.

using System.Text.RegularExpressions;

namespace OpenShiina.Story;

/// <summary>What the story needs from the window around the screen.</summary>
public interface IStoryView
{
    void ShowMessageWindow();

    void HideMessageWindow();

    /// <summary>The speaker's name plate (NWINTBL.BIN) or name; null for none.</summary>
    void SetSpeaker(string? speaker);

    /// <summary>Shows the first <paramref name="shown"/> characters; the rest keeps its place, invisible (MessageLayout).</summary>
    void SetMessageText(string text, int shown);

    /// <summary>The icon that waits for a click after a message.</summary>
    void ShowClickWait(bool visible);

    /// <summary>Auto or skip was turned on or off.</summary>
    void ModesChanged();

    /// <summary>
    /// Shows a choice and returns the option picked, laid out as function 203 of START.SCN does.
    /// With <paramref name="imageSlot"/> &gt;= 0 the options are the game's picture buttons
    /// (SYSTEM.S25 slot + 10 per option); options whose bit is set in <paramref name="played"/>
    /// are dimmed and cannot be chosen.
    /// </summary>
    Task<int> ChooseAsync(IReadOnlyList<string> options, int imageSlot, int played);
}

/// <summary>A message of the backlog.</summary>
public sealed record LogEntry(string Speaker, string Text, string? Voice);

public sealed class StoryPlayer : ScnMachine.IHost, IDisposable
{
    private readonly GameData m_data;
    private readonly IStage m_stage;
    private readonly IStoryView m_view;
    private readonly AudioEngine m_audio;

    // The game's flow script SRC_MAIN.SCN, and the chapters read from it
    private readonly byte[] m_flowCode;
    private StoryOutline? m_outline;

    /// <summary>
    /// Games whose story the player shows like the game does. The title screen, the choice
    /// pictures and the SYSTEM.S25 pages are Oreimo Plus's for now.
    /// </summary>
    public static bool Supports(string schemeName) => schemeName.Equals("Oreimo Plus", StringComparison.OrdinalIgnoreCase);

    public StoryPlayer(GameData data, IStage stage, IStoryView view, AudioEngine audio)
    {
        m_flowCode = data.Read("SRC_MAIN.SCN", ".SCN") ?? throw new InvalidDataException("The game has no SRC_MAIN.SCN.");
        m_data = data;
        m_stage = stage;
        m_view = view;
        m_audio = audio;
    }

    public GameData Data => m_data;
    public AudioEngine Audio => m_audio;

    /// <summary>The game's name, for titles.</summary>
    public string Title => m_data.SchemeName;

    /// <summary>The opening, the routes and the ending as SRC_MAIN lays them out.</summary>
    public StoryOutline Outline => m_outline ??= StoryOutline.Read(m_flowCode);

    // The token of the run that this code belongs to. It flows with the async calls, so a run that
    // was replaced keeps its own cancelled token and stops instead of taking on the new one.
    private readonly AsyncLocal<CancellationToken> m_runToken = new();

    public CancellationToken Token
    {
        get => m_runToken.Value;
        set => m_runToken.Value = value;
    }

    private CancellationToken m_token => Token;

    /// <summary>Releases the sound and the archives, and keeps the record of messages read.</summary>
    public void Dispose()
    {
        if (m_read != null)
            PlayerConfig.SaveRead(m_data.SchemeName, m_read);
        m_audio.Dispose();
        m_data.Dispose();
    }

    #region Modes and input

    private bool m_auto;
    private bool m_skip;
    private bool m_ctrlHeld;

    public bool Auto => m_auto;
    public bool Skip => m_skip;

    /// <summary>Skip mode, Ctrl held, or loading a save (which passes the messages before the saved one like skipping does).</summary>
    public bool Skipping => m_skip || m_ctrlHeld || m_restoreTo >= 0;

    // A click (or Enter, Space, ...) the script is waiting for
    private TaskCompletionSource? m_click;

    // Wakes a message that waits in auto mode when the mode changes
    private TaskCompletionSource? m_modeChanged;

    /// <summary>
    /// Starts a run (the story, or the title screen) with its own token: auto and skip off, and
    /// with <paramref name="restoreTo"/> &gt;= 0 the messages before that one passed silently
    /// (counted by script line with <paramref name="byLine"/>), to come back to a save.
    /// </summary>
    public void BeginRun(CancellationToken token, int restoreTo = -1, bool byLine = false)
    {
        Token = token;
        m_restoreTo = restoreTo;
        m_restoreByLine = byLine;
        m_skip = m_auto = false;
        m_view.ModesChanged();
    }

    /// <summary>A click for the story: ends animations, shows the whole message, or goes on.</summary>
    public void Click() => m_click?.TrySetResult();

    public Task NextClick()
    {
        if (m_click == null || m_click.Task.IsCompleted)
            m_click = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return m_click.Task;
    }

    public void ToggleAuto()
    {
        m_auto = !m_auto;
        if (m_auto)
            m_skip = false;
        m_view.ModesChanged();
        m_modeChanged?.TrySetResult();
    }

    public void ToggleSkip()
    {
        m_skip = !m_skip;
        if (m_skip)
        {
            m_auto = false;
            m_stage.FinishAll();
            m_click?.TrySetResult();
        }
        m_view.ModesChanged();
    }

    /// <summary>Holding Ctrl skips while it is held.</summary>
    public void SetCtrlHeld(bool held)
    {
        if (!held)
        {
            m_ctrlHeld = false;
            m_view.ModesChanged();
            return;
        }
        if (m_ctrlHeld)
            return;
        m_ctrlHeld = true;
        m_view.ModesChanged();
        m_stage.FinishAll();
        m_click?.TrySetResult();
    }

    #endregion

    #region Settings, read messages and backlog

    private PlayerConfig? m_config;
    public PlayerConfig Config => m_config ??= PlayerConfig.Load(m_data.SchemeName);

    private HashSet<string>? m_read;
    private HashSet<string> Read => m_read ??= PlayerConfig.LoadRead(m_data.SchemeName);

    public void SaveConfig() => Config.Save(m_data.SchemeName);

    /// <summary>Volumes from the settings.</summary>
    public void ApplyAudioConfig()
    {
        m_audio.MusicVolume = Config.MusicOn ? (float)Config.MusicVolume : 0;
        m_audio.VoiceVolume = Config.VoiceOn ? (float)Config.VoiceVolume : 0;
        m_audio.EffectVolume = Config.EffectOn ? (float)Config.EffectVolume : 0;
        m_audio.UpdateVolumes();
    }

    /// <summary>Marks a message as read; returns whether it had been read before.</summary>
    private bool MarkRead(string file, int index) => !Read.Add($"{file}:{index}");

    private readonly List<LogEntry> m_log = new();
    private const int LogLimit = 2000;

    /// <summary>The messages shown so far, oldest first (the last 2000).</summary>
    public IReadOnlyList<LogEntry> Log => m_log;

    private void AddLog(string speaker, string text, string? voice)
    {
        m_log.Add(new LogEntry(speaker, text, voice));
        if (m_log.Count > LogLimit)
            m_log.RemoveRange(0, m_log.Count - LogLimit);
    }

    #endregion

    #region Title screen

    /// <summary>
    /// TOPMENU.SCN: with <paramref name="opening"/> the title call (d\CMA002), the Grand Cross
    /// logo (1 s fade, 3 s), white, the caution screen (until a click), white; then the title
    /// picture with the theme. Returns TITLE.S25's frames by slot, for the title buttons.
    /// </summary>
    public async Task<Dictionary<int, S25Frame>> PlayTitleAsync(bool opening)
    {
        if (opening)
        {
            if (m_data.ReadAudio(@"d\CMA002.ogv") is { } call)
                m_audio.Play(AudioEngine.Voice, call, 1);
            foreach (var (file, fade, hold) in new[] { (@"d\logo_gc.s25", 1000, 3000), (@"d\white.s25", 400, 0), (@"d\caution.s25", 600, -1), (@"d\white.s25", 400, 0) })
            {
                m_stage.SetPlane(0, await LoadImageAsync(file));
                await WaitSkippableAsync(m_stage.DrawEx(Transition.CrossFade, fade, null));
                if (hold > 0)
                    await WaitSkippableAsync(Task.Delay(hold, m_token));
                else if (hold < 0)
                    await NextClick().WaitAsync(m_token);
            }
        }
        var frames = (m_data.LoadFrames("TITLE.S25") ?? new()).ToDictionary(f => f.Slot);
        if (frames.TryGetValue(0, out var background))
            m_stage.SetPlane(0, new StageImage(background.Image, background.OffsetX, background.OffsetY));
        await WaitSkippableAsync(m_stage.DrawEx(Transition.CrossFade, 400, null));
        if (await Task.Run(() => m_data.ReadAudio(@"m\oreplus_01.ogv")) is { } theme)
            m_audio.Play(AudioEngine.Music, theme, 0);
        m_token.ThrowIfCancellationRequested();
        return frames;
    }

    #endregion

    // The variables of the scenario files (_D710 ...) are the flow script's a[]
    private int GetVar(int index) => m_machine?.GetA(index) ?? 0;

    private void SetVar(int index, int value) => m_machine?.SetA(index, value);

    #region Flow

    // Where the story is, for saves: routes played, scenario file and message number in it
    private int m_played;
    private string m_file = "";
    private int m_messageIndex;
    private string m_lastText = "";

    // Loading a save: messages before this one are passed silently (-1 = not loading)
    private int m_restoreTo = -1;
    // ... counted by script line (saves that record it) instead of by message number
    private bool m_restoreByLine;
    // Script line of the message shown last
    private int m_messageLine;

    // The interpreter running SRC_MAIN.SCN
    private ScnMachine? m_machine;

    // START.SCN function 203 draws picture choices from SYSTEM.S25 slot 400 (+10 per option)
    private const int ChoiceImageSlot = 400;

    // SRC_MAIN sets b[160] before every scenario file but the first: its first message is auto saved
    private bool m_autoSave;

    /// <summary>
    /// Plays the story by running SRC_MAIN.SCN: from the beginning, from a scenario file (its
    /// scene number and route), or from the variables of a save.
    /// </summary>
    public async Task RunAsync(string? startFile, int initialPlayed = 0, IReadOnlyDictionary<string, int>? vars = null)
    {
        var machine = m_machine = new ScnMachine(m_flowCode);
        if (vars != null)
        {
            machine.Restore(vars);
        }
        else if (startFile != null)
        {
            // The scene number makes SRC_MAIN jump to the file (as after loading a save)
            var scene = machine.FindScenes().FirstOrDefault(s => Same(ScriptStem(s.File), startFile));
            if (scene.File == null)
                throw new InvalidOperationException($"{startFile} is not in SRC_MAIN.SCN.");
            machine.SetB(250, scene.Scene);
            machine.SetA(PlayedVar, Outline.RoutesPlayedAt(startFile, initialPlayed));
        }
        await machine.RunAsync(this, m_token);
    }

    // a[780]: the routes played, a bit per option of the route menu
    private const int PlayedVar = 780;

    private static string ScriptStem(string path) => System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

    Task ScnMachine.IHost.RunScenarioAsync(string file)
    {
        m_played = m_machine?.GetA(PlayedVar) ?? m_played;
        return PlayScriptAsync(ScriptStem(file).ToUpperInvariant());
    }

    async Task<int> ScnMachine.IHost.CallAsync(int function, int[] args)
    {
        // callmod 0,203,#5, mode, ?, choice table, kind (0 text, 1 text with played options
        // greyed, 2 pictures), played mask
        if (function == 203 && args.Length >= 5 && m_machine != null)
        {
            var options = m_machine.ReadChoices(args[2]);
            return await ChooseAsync(options, args[3] >= 2 ? ChoiceImageSlot : -1, args[3] >= 1 ? args[4] : 0);
        }
        return 0;
    }

    private static bool Same(string a, string? b) => b != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private async Task PlayScriptAsync(string file)
    {
        m_file = file;
        m_messageIndex = 0;
        if (m_machine != null)
        {
            // b[160] asks for an auto save at the file's first message
            m_autoSave = m_restoreTo < 0 && m_machine.GetB(160) != 0;
            m_machine.SetB(160, 0);
        }
        var script = await Task.Run(() => m_data.LoadScript(file)).WaitAsync(m_token)
            ?? throw new InvalidOperationException($"The scenario file {file} is missing.");
        await ExecuteAsync(script);
    }

    /// <summary>
    /// Asks a question (see IStoryView.ChooseAsync): skipping stops, the message window goes,
    /// animations end; the answer goes to the backlog.
    /// </summary>
    private async Task<int> ChooseAsync(IReadOnlyList<string> options, int imageSlot = -1, int played = 0)
    {
        m_skip = false;
        m_view.ModesChanged();
        m_view.HideMessageWindow();
        m_stage.FinishAll();
        int result = await m_view.ChooseAsync(options, imageSlot, played);
        AddLog("", $"⇒ {options[result]}", null);
        return result;
    }

    #endregion

    #region Saves

    private SaveStore? m_saves;
    public SaveStore Saves => m_saves ??= new SaveStore(m_data.SchemeName);

    /// <summary>True once the story has reached a scenario file, so there is a place to save.</summary>
    public bool HasPosition => m_file.Length > 0;

    /// <summary>A save of the message on screen.</summary>
    public SaveData CurrentSave() => NewSave(Math.Max(0, m_messageIndex - 1), m_lastText);

    /// <summary>Where the story is now, with the flow script's variables.</summary>
    private SaveData NewSave(int message, string text) =>
        new(m_file, message, m_machine?.GetA(PlayedVar) ?? m_played, text, DateTime.Now) { Vars = m_machine?.Snapshot(), Line = m_messageLine };

    /// <summary>Auto save at the first message of a scenario file (START.SCN function 197).</summary>
    private void AutoSave(int message, string text)
    {
        try
        {
            Saves.WriteAuto(NewSave(message, text), SaveThumbnail());
        }
        catch
        {
            // The story goes on without the auto save
        }
    }

    public const int ThumbnailWidth = 100, ThumbnailHeight = 75;

    /// <summary>
    /// The thumbnail the game stores with a save (START.SCN 2CBF4): the fixed THSAVE.S25 picture
    /// of the topmost plane that shows an event CG of its list (on plane 0 a playing movie counts
    /// instead), with the overlay pictures of that plane and the planes above drawn at their
    /// positions; a scaled screenshot when no plane has one.
    /// </summary>
    public PixelImage SaveThumbnail()
    {
        var tables = m_data.GetThumbnailTables();
        var frames = tables == null ? null : m_data.LoadFrames("d\\thsave.s25")?.ToDictionary(f => f.Slot);
        if (tables == null || frames == null)
            return m_stage.Snapshot(ThumbnailWidth, ThumbnailHeight);

        var planes = m_stage.PictureNames().Where(p => p.Number is >= 0 and <= 9).OrderByDescending(p => p.Number).ToList();
        S25Frame? picture = null;
        int found = -1;
        foreach (var plane in planes)
        {
            int index;
            if (plane.Number == 0 && m_stage.MovieName is { } movie)
                index = Array.IndexOf(tables.Movies, movie) is var m and >= 0 ? 3000 + m : -1;
            else
                index = Array.IndexOf(tables.Pictures, plane.Name);
            if (index >= 0 && frames.TryGetValue(index, out picture))
            {
                found = plane.Number;
                break;
            }
        }
        if (picture == null)
            return m_stage.Snapshot(ThumbnailWidth, ThumbnailHeight);

        var thumbnail = new PixelImage(ThumbnailWidth, ThumbnailHeight);
        thumbnail.DrawOver(picture.Image, picture.OffsetX, picture.OffsetY);
        foreach (var plane in planes.Where(p => p.Number >= found).OrderBy(p => p.Number))
        {
            int index = Array.IndexOf(tables.Overlays, plane.Name);
            if (index >= 0 && frames.TryGetValue(2000 + index, out var overlay))
                thumbnail.DrawOver(overlay.Image, (int)plane.X * ThumbnailWidth / StageSize.Width + overlay.OffsetX,
                                   (int)plane.Y * ThumbnailHeight / StageSize.Height + overlay.OffsetY);
        }
        return thumbnail;
    }

    #endregion

    #region Commands

    private async Task ExecuteAsync(ScenarioScript script)
    {
        var lines = script.Lines;
        int? eventBlockEnd = null;

        for (int pc = 0; pc < lines.Count; pc++)
        {
            m_token.ThrowIfCancellationRequested();

            // Skipping inside an $EVENT_BLOCK jumps to its end label; loading a save plays it
            // silently instead, so that it finds the saved message
            if (eventBlockEnd is int label && Skipping && m_restoreTo < 0 && script.Labels.TryGetValue(label, out int target) && target > pc)
            {
                pc = target;
                eventBlockEnd = null;
                m_stage.FinishAll();
            }

            var line = lines[pc];
            if (line.Text != null)
            {
                await ShowMessageAsync(line, pc);
                continue;
            }

            switch (line.Command)
            {
                case "L_BG":
                    // $L_BG,file,reset,x,y,zoom: reset 0 clears planes 1-9
                    if (line.IntArg(1) == 0)
                        m_stage.ClearCharacters();
                    m_stage.SetPlane(0, await LoadImageAsync(line.Arg(0)), line.IntArg(2), line.IntArg(3));
                    break;

                case "L_CHR":
                case "L_MONT":
                    await LoadPlaneAsync(line.IntArg(0), line);
                    break;

                case "DRAW_EX":
                {
                    // $DRAW_EX,kind,rule,ms,hide window: the engine waits for the transition
                    var kind = line.IntArg(0) switch
                    {
                        1 => Transition.Cut,
                        2 or 37 or 48 => Transition.RuleBrightFirst,
                        47 => Transition.RuleDarkFirst,
                        _ => Transition.CrossFade,
                    };
                    PixelImage? rule = null;
                    if (kind is Transition.RuleBrightFirst or Transition.RuleDarkFirst && !Skipping)
                        rule = (await LoadImageAsync(line.Arg(1)))?.Image;
                    if (line.IntArg(3) != 0)
                        m_view.HideMessageWindow();
                    await WaitSkippableAsync(m_stage.DrawEx(kind, Skipping ? 0 : line.IntArg(2), rule));
                    break;
                }

                case "DRAW":
                    m_stage.Draw();
                    break;

                case "A_CHR":
                    await AnimateCharacterAsync(line);
                    break;

                case "WAITA":
                    await WaitSkippableAsync(m_stage.WhenIdle());
                    break;

                case "WAIT":
                    if (!Skipping)
                        await WaitSkippableAsync(Task.Delay(line.IntArg(0), m_token));
                    break;

                case "WINDOW":
                    if (line.IntArg(0) == 0)
                        m_view.HideMessageWindow();
                    else
                        m_view.ShowMessageWindow();
                    break;

                case "EFECT":
                    if (!Skipping)
                        await WaitSkippableAsync(ScreenEffect(line.IntArg(0)));
                    break;

                case "MUSIC":
                    // $MUSIC,file,loop,fade-in ms
                    if (line.Arg(0).Length == 0)
                        m_audio.Stop(AudioEngine.Music);
                    else if (await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is { } music)
                        m_audio.Play(AudioEngine.Music, music, line.IntArg(1) == 0 ? 1 : 0, line.IntArg(2));
                    break;

                case "MUSIC_FADE":
                    m_audio.Stop(AudioEngine.Music, line.IntArg(0, 1000));
                    break;

                case "SE":
                    await SoundEffectAsync(line);
                    break;

                case "SE_FADE":
                    m_audio.Stop(AudioEngine.Effect(line.IntArg(1)), line.IntArg(0));
                    break;

                case "VOICE":
                    m_audio.Stop(AudioEngine.Voice);
                    m_pendingVoice = line.Arg(0);
                    if (!Skipping && await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is { } voice)
                        m_audio.Play(AudioEngine.Voice, voice, 1);
                    break;

                case "L_MOVIE":
                {
                    string? path = line.Arg(1).Length > 0 ? m_data.LooseFile(line.Arg(1)) : null;
                    m_stage.PlayMovie(line.IntArg(0), path, line.IntArg(2) != 0, m_audio.EffectVolume);
                    break;
                }

                case "WAIT_L_MOVIE":
                    if (!Skipping)
                        await WaitSkippableAsync(m_stage.WhenMovieEnds());
                    break;

                case "EX":
                    await ExtendedAsync(line);
                    break;

                case "LABEL":
                    if (eventBlockEnd == line.IntArg(0))
                        eventBlockEnd = null;
                    break;

                case "CJUMP":
                    if (Condition(line.Arg(0)) && script.Labels.TryGetValue(line.IntArg(1), out int jump))
                        pc = jump - 1;
                    break;

                case "EVENT_BLOCK":
                    eventBlockEnd = line.IntArg(1);
                    break;

                case "PRELOAD":
                {
                    string file = line.Arg(0);
                    if (file.EndsWith(".S25", StringComparison.OrdinalIgnoreCase))
                        _ = Task.Run(() => m_data.LoadFrames(file));
                    break;
                }
            }
        }
        // A scenario file ends with its own fade-out; whatever is still playing stops here
        await WaitSkippableAsync(m_stage.WhenIdle());
    }

    /// <summary>
    /// $L_CHR,plane,file,x,y,type[,m,base,layer1,...] and $L_MONT,plane,file,x,y,?,m|M,...:
    /// "m" lists the slots (value v at position k = slot k*100+v, -1 = off), "M" gives an
    /// expression code from MONTBL.BIN (the file may then be left out).
    /// </summary>
    private async Task LoadPlaneAsync(int plane, ScriptLine line)
    {
        string file = line.Arg(1);
        double x = line.IntArg(2), y = line.IntArg(3);
        int marker = Array.FindIndex(line.Args, 2, a => a is "m" or "M");
        int[]? slots = null;
        bool expression = false;

        if (marker >= 0 && line.Args[marker] == "M")
        {
            if (m_data.GetMontage(line.IntArg(marker + 1)) is { } montage)
            {
                if (file.Length == 0)
                    file = montage.File;
                slots = montage.Slots;
                expression = true;
            }
        }
        else if (marker >= 0)
        {
            var list = new List<int>();
            for (int k = 0; marker + 1 + k < line.Args.Length; k++)
                if (int.TryParse(line.Args[marker + 1 + k], out int v) && v >= 0)
                    list.Add(k * 100 + v);
            slots = list.ToArray();
        }

        if (file.Length == 0)
        {
            m_stage.SetPlane(plane, null);
            return;
        }
        var image = await Task.Run(() => m_data.LoadImage(file, slots)).WaitAsync(m_token);
        if (image == null)
        {
            m_stage.SetPlane(plane, null);
            return;
        }
        // Both go through the action queue (function 304 / 10001): the 5th argument is the plane
        // transition the next $DRAW runs, 0 = a 500 ms cross-fade
        if (expression)
            m_stage.ChangePicture(plane, image, x, y);
        else
            m_stage.SetPlane(plane, image, x, y);
        PlaneTransition(plane, line.IntArg(4), x, y, -1, false);
    }

    private async Task<StageImage?> LoadImageAsync(string file) =>
        file.Length == 0 ? null : await Task.Run(() => m_data.LoadImage(file)).WaitAsync(m_token);

    // Function 306 of START.SCN: plane transition type -> kind (table at 0x680F0)
    private static readonly int[] s_transitionKinds = { 0, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6, 6, 2, 3, 3, 4, 5, 6, 1, 7, 7, 7, 7, 8, 8, 8, 8, 5, 7 };

    // Where a slide starts (slide in) or ends (slide out); types 14, 28 and 29 move from the current position
    private static readonly Dictionary<int, (int X, int Y)> s_slideOffsets = new()
    {
        [1] = (0, 800), [2] = (-800, 0), [3] = (800, 0), [15] = (0, -800),
        [4] = (0, 800), [5] = (800, 0), [6] = (-800, 0), [16] = (0, -800),
        [7] = (0, 800), [8] = (-800, 0), [9] = (800, 0), [17] = (0, -800),
        [10] = (0, 800), [11] = (800, 0), [12] = (-800, 0), [18] = (0, -800),
        [20] = (0, 800), [21] = (0, -800), [22] = (-800, 0), [23] = (800, 0),
        [24] = (0, -800), [25] = (0, 800), [26] = (-800, 0), [27] = (800, 0),
    };

    /// <summary>
    /// Function 306: a plane transition of <paramref name="type"/> (0-29) to (x, y) over
    /// <paramref name="ms"/> (-1 = the type's default). 0 cross-fade, 19 fade in, 13 fade out and
    /// remove, others slide in / out or move with easing 1 (linear), 3 (slow end) or 2 (slow start).
    /// </summary>
    private void PlaneTransition(int plane, int type, double x, double y, int ms, bool background)
    {
        if (type < 0 || type >= s_transitionKinds.Length)
            type = 0;
        int kind = s_transitionKinds[type];
        bool fromCurrent = type is 14 or 28 or 29;
        double Time(int fallback) => ms >= 0 ? ms : fallback;
        int easing = kind switch { 3 or 4 => 1, 5 or 6 => 3, _ => 2 };
        s_slideOffsets.TryGetValue(type, out var offset);

        switch (kind)
        {
            case 0:
                m_stage.QueueCrossFade(plane, Time(500), background);
                break;
            case 1:
                m_stage.QueueFadeIn(plane, Time(1000), background);
                break;
            case 2:
                m_stage.QueueFadeOut(plane, Time(1000), background);
                break;
            case 3 or 5 or 7:   // slide in, or move from the current position
                m_stage.QueueMove(plane, fromCurrent ? null : offset.X, fromCurrent ? null : offset.Y, x, y, easing,
                                  Time(fromCurrent ? 500 : 1000), background, removeAtEnd: false);
                break;
            case 4 or 6 or 8:   // slide out, then remove the plane
                m_stage.QueueMove(plane, x, y, offset.X, offset.Y, easing, Time(1000), background, removeAtEnd: true);
                break;
        }
    }

    /// <summary>
    /// $A_CHR,code,plane,...: plane animations, started by the next $DRAW. The last argument
    /// (wf) of most codes makes a background animation that $WAITA does not wait for.
    /// </summary>
    private async Task AnimateCharacterAsync(ScriptLine line)
    {
        int code = line.IntArg(0), plane = line.IntArg(1);
        int P(int i) => line.IntArg(2 + i);
        switch (code)
        {
            case 0:             // stop the loop at the end of its cycle
            case 9:             // stop the loop now
                m_stage.QueueStopLoop(plane, code == 9);
                break;
            case >= 1 and <= 6: // loop: cycles (0 = forever), amplitude, period
                m_stage.QueueLoop(plane, code, P(0), P(1), P(2));
                break;
            case 40:            // screen area the plane is drawn into
                m_stage.SetViewTarget(plane, new StageRect(P(0), P(1), P(2), P(3)));
                break;
            case 41:            // part of the plane shown in that area
                m_stage.SetViewSource(plane, new StageRect(P(0), P(1), P(2), P(3)));
                break;
            case >= 42 and <= 44:   // pan / zoom: x, y, w, h, ms, wf; easing 1-3
                m_stage.QueueView(plane, new StageRect(P(0), P(1), P(2), P(3)), code - 41, P(4), P(5) != 0);
                break;
            case >= 60 and <= 63:   // through a rule mask: rule, ms; 60/62 appear, 61/63 disappear, 62/63 reversed
                if (await LoadImageAsync(line.Arg(2)) is { } rule)
                    m_stage.QueueRuleFade(plane, rule.Image, code % 2 == 0, code >= 62, line.IntArg(3), false);
                break;
            case 90:            // play sound channel n at every cycle of the loop (footsteps)
            {
                int channel = P(0);
                m_stage.QueueCycleAction(plane, () => PlaySoundSlot(channel));
                break;
            }
            case 91:
                m_stage.QueueCycleAction(plane, null);
                break;
            case >= 100 and <= 129: // function 306: x, y, ms, wf
                PlaneTransition(plane, code - 100, P(0), P(1), P(2), P(3) != 0);
                break;
            case 150:           // fade out (then remove), fade in, cross-fade: ms, wf
            case 151:
            case 152:
                if (m_stage.TryGetPosition(plane, out double x, out double y))
                    PlaneTransition(plane, code == 150 ? 13 : code == 151 ? 19 : 0, x, y, P(0), P(1) != 0);
                break;
        }
    }

    /// <summary>$EFECT,n (function 217 of START.SCN, effects of EFCLIB.SCN). The engine waits for it.</summary>
    private Task ScreenEffect(int effect)
    {
        // EFCLIB 35 zoom pulse sizes: (pixels a side per step x, y, rounds)
        (int, int, int)[] pulses = { (8, 6, 2), (16, 12, 2), (32, 24, 2), (4, 3, 2), (8, 6, 1), (16, 12, 1), (32, 24, 1), (4, 3, 1) };
        int[] pulseOf = { 1, 2, 0, 3, 5, 6, 4, 7 };   // $EFECT 8-15
        switch (effect)
        {
            case 0: return m_stage.Shake(16);
            case 1: return m_stage.Shake(32);
            case 2: return m_stage.Shake(8);
            case >= 8 and <= 15:
            {
                var (w, h, rounds) = pulses[pulseOf[effect - 8]];
                return m_stage.ZoomPulse(w, h, rounds);
            }
            case 3: return m_stage.Negative(50);
            case 4: return m_stage.Flash(StageColor.White, 50, fade: false);
            case 5: return m_stage.Flash(StageColor.Red, 50, fade: false);
            case 6: return m_stage.Negative(1000);
            default: return Task.CompletedTask;
        }
    }

    // Sounds loaded into the effect channels ($SE), replayed by A_CHR 90
    private readonly Dictionary<int, byte[]> m_soundSlots = new();

    /// <summary>
    /// $SE,file,mode,channel: mode 0 plays once, 1 loops, 2 plays once and waits, 3 only loads
    /// the sound (for A_CHR 90). No file stops the channel.
    /// </summary>
    private async Task SoundEffectAsync(ScriptLine line)
    {
        int mode = line.IntArg(1), channel = line.IntArg(2);
        string name = AudioEngine.Effect(channel);
        if (line.Arg(0).Length == 0)
        {
            m_audio.Stop(name);
            return;
        }
        if (await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is not { } sound)
            return;
        m_soundSlots[channel] = sound;
        if (mode == 3 || (Skipping && mode != 1))
            return;
        m_audio.Play(name, sound, mode == 1 ? 0 : 1);
        if (mode == 2)
            await WaitSkippableAsync(WhenSoundEnds(name));
    }

    private void PlaySoundSlot(int channel)
    {
        if (!Skipping && m_soundSlots.TryGetValue(channel, out var sound))
            m_audio.Play(AudioEngine.Effect(channel), sound, 1);
    }

    private async Task WhenSoundEnds(string channel)
    {
        while (m_audio.IsPlaying(channel))
            await Task.Delay(50, m_token);
    }

    /// <summary>$EX,group,...: background scroll (9), variables (10) and key waits (2).</summary>
    private async Task ExtendedAsync(ScriptLine line)
    {
        switch (line.IntArg(0))
        {
            case 9:
                switch (line.IntArg(1))
                {
                    case 0: m_stage.ScrollInit(line.IntArg(3)); break;             // count, width
                    case 1:                                                          // slot, file
                        if (await LoadImageAsync(line.Arg(3)) is { } picture)
                            m_stage.ScrollImage(picture);
                        break;
                    case 2: m_stage.ScrollStart(line.IntArg(2, 10)); break;          // pixels per second
                    case 3: m_stage.ScrollStart(0); break;
                    case 4: m_stage.ScrollStop(); break;
                }
                break;
            case 10 when line.IntArg(1) == 2:
                SetVar(line.IntArg(2), line.IntArg(3));
                break;
            case 2:
                if (!Skipping)
                    await NextClick().WaitAsync(m_token);
                break;
        }
    }

    /// <summary>"_D710==0" style conditions of $CJUMP.</summary>
    private bool Condition(string text)
    {
        var m = Regex.Match(text, @"^_D(\d+)\s*(==|!=|>=|<=|>|<)\s*(-?\d+)$");
        if (!m.Success)
            return false;
        int value = GetVar(int.Parse(m.Groups[1].Value));
        int other = int.Parse(m.Groups[3].Value);
        return m.Groups[2].Value switch
        {
            "==" => value == other,
            "!=" => value != other,
            ">=" => value >= other,
            "<=" => value <= other,
            ">" => value > other,
            _ => value < other,
        };
    }

    /// <summary>Waits for <paramref name="task"/>; a click (or skipping) ends the animations at once.</summary>
    private async Task WaitSkippableAsync(Task task)
    {
        if (task.IsCompleted)
            return;
        if (Skipping)
        {
            m_stage.FinishAll();
            return;
        }
        var click = NextClick();
        var done = await Task.WhenAny(task, click).WaitAsync(m_token);
        if (done != task)
            m_stage.FinishAll();
    }

    #endregion

    #region Messages

    // Voice file of the message being shown, for the backlog
    private string? m_pendingVoice;

    private async Task ShowMessageAsync(ScriptLine line, int pc)
    {
        string text = line.Text!;
        string? voice = m_pendingVoice;
        m_pendingVoice = null;
        AddLog(line.Speaker ?? "", ScenarioScript.DisplayText(text), voice);

        // Loading a save: pass the messages before the saved one
        int index = m_messageIndex++;
        m_lastText = text;
        m_messageLine = pc;
        if (m_restoreTo >= 0)
        {
            if ((m_restoreByLine ? pc : index) < m_restoreTo)
                return;
            m_restoreTo = -1;
            m_view.ModesChanged();
        }

        // メッセージスキップ 既読: skipping stops at a message never read before
        bool readBefore = MarkRead(m_file, index);
        if (m_skip && !readBefore && !Config.SkipUnread)
        {
            m_skip = false;
            m_view.ModesChanged();
        }

        m_view.SetSpeaker(line.Speaker);
        m_view.ShowMessageWindow();
        m_view.ShowClickWait(false);
        if (m_autoSave)
        {
            m_autoSave = false;
            AutoSave(index, text);
        }

        if (Skipping)
        {
            m_view.SetMessageText(text, text.Length);
            await Task.Delay(15, m_token);
            return;
        }

        // Type the text out; a click shows the rest at once
        var click = NextClick();
        int characterMs = Config.CharacterMs;
        for (int shown = 1; characterMs > 0 && shown < text.Length; shown++)
        {
            m_view.SetMessageText(text, shown);
            if (await Task.WhenAny(Task.Delay(characterMs, m_token), click) == click || Skipping)
                break;
            m_token.ThrowIfCancellationRequested();
        }
        m_view.SetMessageText(text, text.Length);
        m_token.ThrowIfCancellationRequested();
        m_view.ShowClickWait(true);

        // Wait for a click; in auto mode go on once the voice has ended and the text had time to be read
        while (!Skipping)
        {
            click = NextClick();
            if (!m_auto)
            {
                m_modeChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (await Task.WhenAny(click, m_modeChanged.Task).WaitAsync(m_token) == click)
                    break;
                continue;
            }
            while (m_audio.IsPlaying(AudioEngine.Voice) && m_auto && !click.IsCompleted)
                await Task.Delay(100, m_token);
            if (click.IsCompleted)
                break;
            var read = Task.Delay(Config.AutoDelay(text.Length, voice != null), m_token);
            if (await Task.WhenAny(read, click) == click || m_auto)
                break;
        }

        m_view.ShowClickWait(false);
        if (!Skipping)
            m_audio.Stop(AudioEngine.Voice);
    }

    #endregion
}
