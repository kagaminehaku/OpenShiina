// Message text drawn the way the engine's text system does: the positions come from the engine
// library's MessageLayout (ＭＳ ゴシック 24 px, fixed advances, kinsoku), white, no edge by
// default, and gaiji characters drawn from GAIJI.S25.

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OpenShiina.Windows;

public sealed class MessageText : FrameworkElement
{
    public const int FullAdvance = MessageLayout.FullAdvance, HalfAdvance = MessageLayout.HalfAdvance,
                     LineHeight = MessageLayout.LineHeight, FontHeight = MessageLayout.FontHeight;

    private static readonly Typeface s_face = new(new FontFamily("MS Gothic, ＭＳ ゴシック, Yu Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // Glyphs are the same for every message: lay each character out once
    private readonly Dictionary<char, FormattedText> m_glyphs = new();

    private string m_text = "";
    private int m_shown;

    /// <summary>Gaiji picture for a character (① = GAIJI.S25 frame 0, ...), or null.</summary>
    public Func<char, S25Frame?>? Gaiji { get; set; }

    public Brush Foreground { get; set; } = Brushes.White;

    /// <summary>Sets the message and how many of its characters are visible.</summary>
    public void SetText(string text, int shown)
    {
        if (text == m_text && shown == m_shown)
            return;
        m_text = text;
        m_shown = Math.Clamp(shown, 0, text.Length);
        InvalidateVisual();
    }

    private double LayoutWidth => ActualWidth > 0 ? ActualWidth : Width;

    /// <summary>How many lines the text takes at the element's width.</summary>
    public int LineCount => MessageLayout.LineCount(m_text, LayoutWidth);

    protected override void OnRender(DrawingContext dc)
    {
        if (m_text.Length == 0)
            return;
        var positions = MessageLayout.Layout(m_text, LayoutWidth);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < m_shown; i++)
        {
            char c = m_text[i];
            if (c == '\n')
                continue;
            var (x, y) = positions[i];
            if (MessageLayout.GaijiIndex(c) >= 0 && Gaiji?.Invoke(c) is { } frame)
            {
                dc.DrawImage(frame.Image.ToBitmapSource(), new Rect(x + frame.OffsetX, y + frame.OffsetY, frame.Width, frame.Height));
                continue;
            }
            if (!m_glyphs.TryGetValue(c, out var glyph))
                m_glyphs[c] = glyph = new FormattedText(c.ToString(), CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                                                        s_face, FontHeight, Foreground, dpi);
            dc.DrawText(glyph, new Point(x, y));
        }
    }
}
