// Test harness for the Windows player; see OpenShiina.Harness.csproj.
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenShiina.Archives;
using OpenShiina.Audio;
using OpenShiina.Formats;
using OpenShiina.Story;
using OpenShiina.Windows;

static class Program
{
    static StreamWriter log;
    static PlayerWindow w;
    static T F<T>(string name) => (T)typeof(PlayerWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).GetValue(w);
    static void SetF(string name, object v) => typeof(PlayerWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(w, v);
    static object Call(string name, params object[] a)
    {
        var m = typeof(PlayerWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        var all = m.GetParameters().Select((p, i) => i < a.Length ? a[i] : p.HasDefaultValue ? p.DefaultValue : Type.Missing).ToArray();
        return m.Invoke(w, all);
    }
    // The story engine behind the window (OpenShiina.Core)
    static StoryPlayer player;
    static T PF<T>(string name) => (T)typeof(StoryPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(player);
    static void SetPF(string name, object v) => typeof(StoryPlayer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(player, v);
    static object CallP(string name, params object[] a) => typeof(StoryPlayer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Invoke(player, a);

    [STAThread]
    static void Main(string[] args)
    {
        // args: mode(skip|play) startFile|- outDir clicks shotEvery
        string seek = args[0].StartsWith("seek:") ? args[0][5..] : null; string mode = seek != null ? "skip" : args[0], start = args[1] == "-" ? null : args[1], outDir = args[2];
        int maxClicks = int.Parse(args[3]), shotEvery = int.Parse(args[4]);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENSHIINA_DATA", Path.Combine(Path.GetFullPath(outDir), "data"));
        if (!PlayerFolders.Root.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("player data folder not redirected");
        log = new StreamWriter(Path.Combine(outDir, "log.txt")) { AutoFlush = true };
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is OperationCanceledException) return;
            log.WriteLine($"[first-chance] {e.Exception.GetType().Name}: {e.Exception.Message}");
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { log.WriteLine("UNHANDLED " + e.Exception); e.Handled = true; };
        // The game: OPENSHIINA_GAME_DIR, or games\GrandCross\俺妹プラス in a folder above this program (GrandCrossMove), or the default install
        string gameDir = Environment.GetEnvironmentVariable("OPENSHIINA_GAME_DIR");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); gameDir == null && d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "games", "GrandCross", "俺妹プラス")))
                gameDir = Path.Combine(d.FullName, "games", "GrandCross", "俺妹プラス");
        gameDir ??= @"C:\Program Files (x86)\GrandCross\俺妹プラス";
        var data = GameData.Open(gameDir, FormatManager.Instance.GetScheme("Oreimo Plus"));
        w = new PlayerWindow(data) { Left = -20000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        player = F<StoryPlayer>("m_player");
        var audio = player.Audio; audio.MusicVolume = audio.VoiceVolume = audio.EffectVolume = 0;
        w.Show();
        player.Config.SkipUnread = true;
        var screen = F<Grid>("Screen");
        var msg = F<MessageText>("TxtMessage");
        var textField = typeof(MessageText).GetField("m_text", BindingFlags.NonPublic | BindingFlags.Instance);
        var choice = F<Canvas>("ChoiceLayer");
        var menu = F<Grid>("MenuLayer");
        var title = F<Canvas>("TitleLayer");
        bool storyStarted = false;
        int clicks = 0, shots = 0, choices = 0; string last = "";
        var started = DateTime.Now;
        void Shot(string tag)
        {
            screen.UpdateLayout();
            var rtb = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(screen);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(outDir, $"{shots++:D3}_{tag}.png")); enc.Save(fs);
        }
        if (mode == "menu") { var t0 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) }; t0.Tick += (_, _) => { Call("ShowMenu"); screen.UpdateLayout(); Shot("menu"); w.Close(); app.Shutdown(); }; t0.Start(); app.Run(); return; }
        if (mode == "rulefade")
        {
            // A_CHR 62,5,RULE33,1000 over a background, and EFECT 6 (negative)
            var canvas = new Canvas();
            var stage = new Stage(canvas);
            var sw = new Window { Content = canvas, SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = 0, ShowActivated = false, WindowStyle = WindowStyle.None };
            sw.Show();
            void Shot2(string tag)
            {
                canvas.UpdateLayout();
                var rtb = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(canvas);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(outDir, $"{tag}.png")); enc.Save(fs);
            }
            stage.SetPlane(0, data.LoadImage("e\\ev02_07b"));
            stage.SetPlane(5, data.LoadImage("e\\ev08_26"));
            stage.QueueRuleFade(5, data.LoadImage("c\\RULE33").Image, appear: true, reversed: true, 2000, background: false);
            stage.Draw();
            int ticks = 0;
            var tr = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            tr.Tick += (_, _) => { ticks++; if (ticks <= 4) Shot2($"rule{ticks}"); if (ticks == 6) _ = stage.Negative(1000); if (ticks == 7) Shot2("negative"); if (ticks == 10) { Shot2("after"); sw.Close(); w.Close(); app.Shutdown(); } };
            tr.Start(); app.Run(); return;
        }
        if (mode == "thumb")
        {
            var t = data.GetThumbnailTables();
            log.WriteLine($"tables {t?.Pictures.Length} {t?.Movies.Length} {t?.Overlays.Length}: {string.Join(",", t?.Pictures.Take(2) ?? [])} .. {t?.Pictures.LastOrDefault()} | {string.Join(",", t?.Movies.Take(2) ?? [])} | {string.Join(",", t?.Overlays ?? [])}");
            var stage = F<Stage>("m_stage");
            void Thumb(string tag)
            {
                File.WriteAllBytes(Path.Combine(outDir, $"thumb_{tag}.png"), PngEncoder.Encode(player.SaveThumbnail()));
            }
            stage.SetPlane(0, data.LoadImage("e\\ev02_07b")); Thumb("ev02_07b");
            stage.SetPlane(0, data.LoadImage("e\\ev08_26")); stage.SetPlane(3, data.LoadImage("e\\ev08_27"), 400, 0); Thumb("ev08_26_overlay");
            stage.Reset(); stage.SetPlane(0, data.LoadImage("b\\bg01")); Thumb("bg_screenshot");
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) CallP("AutoSave", 0, "test");
            log.WriteLine($"20 auto saves: {sw0.ElapsedMilliseconds} ms");
            w.Close(); app.Shutdown(); return;
        }
        if (mode == "pages")
        {
            int ticks = 0;
            var tp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            tp.Tick += (_, _) => { ticks++; if (ticks == 2) { Call("ShowSavePage", false); SetF("m_savePage", 9); Call("BuildSavePage"); } if (ticks == 3) Shot("autopage"); if (ticks == 4) { SetF("m_savePage", 0); Call("BuildSavePage"); } if (ticks == 5) { Shot("page1"); w.Close(); app.Shutdown(); } };
            tp.Start(); app.Run(); return;
        }
        if (mode == "title")
        {
            int ticks = 0;
            var tt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            tt.Tick += (_, _) => { ticks++; if (ticks == 3) Shot("logo"); if (ticks >= 9 && ticks < 16) { if (ticks == 10) Shot("caution"); Call("Advance"); } if (ticks == 20) { Shot("title"); Call("ShowOption"); } if (ticks == 21) { Shot("title_option"); Call("HideOption"); Call("ShowSavePage", false); } if (ticks == 22) { Shot("title_load"); Call("HideSavePage"); Call("QuitGame"); } if (ticks == 23) { Shot("dialog"); Call("CloseDialog", false); } if (ticks == 24) { Shot("after_dialog"); w.Close(); app.Shutdown(); } };
            tt.Start(); app.Run(); return;
        }
        w.ContentRendered += (_, _) => app.Dispatcher.BeginInvoke(() => { Call("Start", start); if (mode == "skip") SetPF("m_skip", true); }, DispatcherPriority.Background);

        bool msgSeen(ref bool s) { if (title.Visibility != Visibility.Visible) s = true; return s; }
        object lastChoice = null;
        // Watchdog: no new message or choice for 30 s means the run is stuck
        var lastProgress = DateTime.Now; string watchText = null;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watchdog.Tick += (_, _) =>
        {
            string now = (string)textField.GetValue(msg);
            if (now != watchText || choice.Visibility == Visibility.Visible) { watchText = now; lastProgress = DateTime.Now; return; }
            if ((DateTime.Now - lastProgress).TotalSeconds < 30) return;
            log.WriteLine($"STUCK: no progress for 30 s after {choices} choices; last text: {now}");
            Shot("stuck"); w.Close(); app.Shutdown();
        };
        watchdog.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(mode == "skip" ? 50 : 350) };
        timer.Tick += (_, _) =>
        {
            if (msgSeen(ref storyStarted) && (menu.Visibility == Visibility.Visible || title.Visibility == Visibility.Visible))
            {
                log.WriteLine($"MENU shown after {clicks} clicks, {choices} choices, {(DateTime.Now - started).TotalSeconds:F0}s");
                Shot("end"); timer.Stop();
                Call("ShowSavePage", false); SetF("m_savePage", 9); Call("BuildSavePage"); Shot("autopage");
                SetF("m_savePage", 0); Call("BuildSavePage"); Shot("page1");
                w.Close(); app.Shutdown(); return;
            }
            if (choice.Visibility == Visibility.Visible && choice.Children.Count > 0 && choice.Children[0] == lastChoice) return;
            if (choice.Visibility == Visibility.Visible && choice.Children.Count > 0)
            {
                lastChoice = choice.Children[0];
                Shot("choice");
                var first = choice.Children.OfType<FrameworkElement>().First(c => c.Cursor == Cursors.Hand);
                log.WriteLine($"choice #{choices++}: {choice.Children.Count} options, {choice.Children.OfType<FrameworkElement>().Count(c => c.Cursor == Cursors.Hand)} enabled");
                first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                if (mode == "skip") Dispatcher.CurrentDispatcher.BeginInvoke(() => SetPF("m_skip", true), DispatcherPriority.Background);
                return;
            }
            string text = (string)textField.GetValue(msg);
            if (seek != null && text.Contains(seek)) { log.WriteLine("seek reached: " + text); seek = null; mode = "play"; SetPF("m_skip", false); timer.Interval = TimeSpan.FromMilliseconds(900); }
            if (text != last) { last = text; }
            if (mode == "ui" && clicks == 8) { Shot("bar"); Call("ShowLog"); mode = "ui_log"; return; }
            if (mode == "ui_log") { Shot("log"); Call("HideLog"); Call("ShowOption"); mode = "ui_opt"; return; }
            if (mode == "ui_opt") { Shot("option"); timer.Stop(); w.Close(); app.Shutdown(); return; }
            if (mode == "save" && clicks == 20)
            {
                Shot("before"); log.WriteLine("before: " + text);
                Call("ShowSavePage", true); mode = "saving"; return;
            }
            if (mode == "saving")
            {
                Shot("savepage");
                Call("SlotClicked", 99, null); Call("BuildSavePage"); mode = "saved"; return;
            }
            if (mode == "saved")
            {
                Shot("savepage2"); Call("HideSavePage");
                var data = player.Saves.Read(99);
                log.WriteLine($"saved: {data} vars: {string.Join(" ", data.Vars?.Select(v => $"{v.Key}={v.Value}") ?? [])}");
                Call("Load", data);
                clicks++; mode = "restored"; return;
            }
            if (mode == "restored") { log.WriteLine($"  restore={PF<int>("m_restoreTo")} byLine={PF<bool>("m_restoreByLine")} file={PF<string>("m_file")} idx={PF<int>("m_messageIndex")} line={PF<int>("m_messageLine")}"); if (++clicks == 32) { Shot("restored"); log.WriteLine("after: " + text); timer.Stop(); w.Close(); app.Shutdown(); } return; }
            if (mode == "play" || mode == "ui" || (mode == "save" && clicks < 20))
            {
                if (shotEvery > 0 && clicks % shotEvery == 0) Shot($"c{clicks}");
                Call("Advance");
                clicks++;
                if (clicks >= maxClicks) { log.WriteLine($"stopped after {clicks} clicks"); timer.Stop(); w.Close(); app.Shutdown(); }
            }
            else if ((DateTime.Now - started).TotalSeconds > 900) { log.WriteLine("TIMEOUT; last text: " + last); timer.Stop(); w.Close(); app.Shutdown(); }
        };
        timer.Start();
        app.Run();
    }
}
