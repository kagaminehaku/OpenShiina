// The engine's 800x600 screen as the story interpreter sees it: numbered picture planes
// (0 = background), a scrolling background, a movie, transitions, plane animations and screen
// effects. Each front end draws it its own way (the WPF app: Player/Stage.cs); what each call
// means is written here and in docs/engine-notes.md, section 8, and the formulas are in StageMath.
//
// Drawing commands change the planes at once, but the screen keeps showing the previous picture
// until Draw / DrawEx shows the change. Plane animations (the Queue... calls) wait for that Draw,
// as the engine's action queue does.


namespace OpenShiina.Story;

public static class StageSize
{
    public const int Width = 800, Height = 600;
}

/// <summary>How $DRAW_EX replaces the screen.</summary>
public enum Transition
{
    Cut,
    CrossFade,
    /// <summary>Rule wipe, bright parts of the rule mask first ($DRAW_EX kind 2).</summary>
    RuleBrightFirst,
    /// <summary>Rule wipe, dark parts first ($DRAW_EX kind 47).</summary>
    RuleDarkFirst,
}

/// <summary>A rectangle in screen or plane pixels.</summary>
public readonly record struct StageRect(double X, double Y, double Width, double Height)
{
    public static StageRect Screen => new(0, 0, StageSize.Width, StageSize.Height);
}

public readonly record struct StageColor(byte R, byte G, byte B)
{
    public static StageColor White => new(255, 255, 255);
    public static StageColor Red => new(255, 0, 0);
}

/// <summary>A screen transform: scale, then move (x' = x * ScaleX + OffsetX).</summary>
public readonly record struct StageTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY);

public interface IStage
{
    #region Planes

    /// <summary>
    /// Shows <paramref name="picture"/> on a plane at (x, y). A plane that keeps showing a
    /// picture keeps its looping animation. Null clears the plane.
    /// </summary>
    void SetPlane(int number, StageImage? picture, double x = 0, double y = 0);

    /// <summary>Replaces a plane's picture but keeps its position and animations (expression change).</summary>
    void ChangePicture(int number, StageImage picture, double x, double y);

    /// <summary>Clears planes 1-9 ($L_BG with reset 0 starts a new scene).</summary>
    void ClearCharacters();

    /// <summary>Planes showing a picture: number, picture name and position, for save thumbnails.</summary>
    IEnumerable<(int Number, string Name, double X, double Y)> PictureNames();

    bool TryGetPosition(int number, out double x, out double y);

    #endregion

    #region Animations

    /// <summary>
    /// A click: ends the transition and every animation that is not a background one (wf = 1),
    /// including loops with a number of cycles. Endless loops keep running.
    /// </summary>
    void FinishAll();

    /// <summary>Completes when the animations $WAITA waits for, and a non-looping movie, have ended.</summary>
    Task WhenIdle();

    /// <summary>
    /// Cross-fades a plane from what it showed at the last $DRAW to its new picture (function
    /// 306 type 0: $L_CHR, A_CHR 152). A plane that was empty fades in.
    /// </summary>
    void QueueCrossFade(int number, double ms, bool background);

    /// <summary>Fades a plane in from transparent (function 306 type 19, A_CHR 151).</summary>
    void QueueFadeIn(int number, double ms, bool background);

    /// <summary>Fades a plane out, then removes it (function 306 type 13, A_CHR 150).</summary>
    void QueueFadeOut(int number, double ms, bool background);

    /// <summary>
    /// Moves a plane from (fromX, fromY) - its current position when null - to (x, y) with one
    /// of the engine's easings (StageMath.Ease), optionally removing it at the end (slide out).
    /// </summary>
    void QueueMove(int number, double? fromX, double? fromY, double x, double y, int easing, double ms, bool background, bool removeAtEnd);

    /// <summary>Sets the part of the screen a plane is drawn into (A_CHR 40).</summary>
    void SetViewTarget(int number, StageRect target);

    /// <summary>Sets the part of the plane that is shown, zoomed to the target (A_CHR 41).</summary>
    void SetViewSource(int number, StageRect source);

    /// <summary>Pans / zooms the shown part to <paramref name="source"/> (A_CHR 42-44: easing 1-3).</summary>
    void QueueView(int number, StageRect source, int easing, double ms, bool background);

    /// <summary>
    /// A_CHR 1-6: the plane moves in a loop (StageMath.LoopOffset). <paramref name="cycles"/> = 0
    /// loops until stopped, <paramref name="amplitude"/> in pixels, <paramref name="period"/> in ms
    /// (at least 30).
    /// </summary>
    void QueueLoop(int number, int mode, int cycles, double amplitude, double period);

    /// <summary>A_CHR 00 stops the loop at the end of its current cycle, A_CHR 09 at once.</summary>
    void QueueStopLoop(int number, bool now);

    /// <summary>A_CHR 90 / 91: run <paramref name="onCycle"/> at every cycle of the plane's loop (footsteps).</summary>
    void QueueCycleAction(int number, Action? onCycle);

    /// <summary>
    /// A_CHR 60-63: the plane appears (or disappears, then is removed) through a rule mask
    /// (StageMath.RuleFadeLevels); <paramref name="reversed"/> swaps the mask's bright and dark parts.
    /// </summary>
    void QueueRuleFade(int number, PixelImage rule, bool appear, bool reversed, double ms, bool background);

    #endregion

    #region Commit and transitions

    /// <summary>
    /// $DRAW: shows the prepared picture at once and starts the queued animations. Planes
    /// changed by $L_CHR cross-fade on their own.
    /// </summary>
    void Draw();

    /// <summary>
    /// $DRAW_EX: replaces the screen with the prepared picture (cut, cross-fade or rule wipe
    /// over <paramref name="ms"/>, StageMath.RuleWipeLevels) and starts the queued animations;
    /// queued plane cross-fades are dropped. The task completes when the transition has ended.
    /// </summary>
    Task DrawEx(Transition kind, double ms, PixelImage? rule);

    #endregion

    #region Screen effects ($EFECT, EFCLIB.SCN)

    /// <summary>EFCLIB 34 ($EFECT 0 / 1 / 2): StageMath.ShakeFrames at 15 fps.</summary>
    Task Shake(int size);

    /// <summary>EFCLIB 35 ($EFECT 8-15): StageMath.ZoomPulseFrames at 15 fps.</summary>
    Task ZoomPulse(int w, int h, int rounds);

    /// <summary>A flash of <paramref name="color"/> held for <paramref name="ms"/> ($EFECT 4 / 5).</summary>
    Task Flash(StageColor color, double ms, bool fade);

    /// <summary>EFCLIB 21 with 255 ($EFECT 3 / 6): the screen's negative held for <paramref name="ms"/>.</summary>
    Task Negative(double ms);

    #endregion

    #region Background scroll

    /// <summary>Prepares a horizontally scrolling background above plane 0 ($EX,9,0,count,width).</summary>
    void ScrollInit(int width);

    /// <summary>Sets the scrolling picture; it is drawn twice side by side so it wraps ($EX,9,1,slot,file).</summary>
    void ScrollImage(StageImage picture);

    /// <summary>Starts scrolling ($EX,9,2,speed): pixels per second; a positive speed moves the picture right.</summary>
    void ScrollStart(double speed);

    /// <summary>Removes the scrolling background ($EX,9,4).</summary>
    void ScrollStop();

    #endregion

    #region Movie

    /// <summary>Plays an MPEG movie file on a plane ($L_MOVIE,plane,file,loop,...). Null stops it.</summary>
    void PlayMovie(int plane, string? path, bool loop, double volume);

    /// <summary>File stem of the movie playing, or null.</summary>
    string? MovieName { get; }

    void StopMovie();

    /// <summary>Completes when the movie reaches its end (the end of the current round for a loop).</summary>
    Task WhenMovieEnds();

    void PauseMovie(bool pause);

    #endregion

    /// <summary>What the screen shows now, scaled to <paramref name="width"/> x <paramref name="height"/> (BGRA).</summary>
    PixelImage Snapshot(int width, int height);

    /// <summary>Clears everything (a new chapter starts).</summary>
    void Reset();
}
