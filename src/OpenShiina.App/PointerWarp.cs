// Moving the system's mouse pointer, as SetCursorPos does for the original (0457: the menus put
// the pointer on the item chosen with the keyboard, since they choose the item under the
// pointer). Avalonia cannot move the pointer, so each desktop platform's own call: SetCursorPos
// on Windows, XWarpPointer on X11, CGWarpMouseCursorPosition on macOS. Wayland lets no program
// move the pointer, and phones and tablets have none: there the scripts only see the position
// they set, until the pointer or a finger moves.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace OpenShiina.App;

internal static class PointerWarp
{
    private static IntPtr s_display;
    private static bool s_noDisplay;

    /// <summary>Moves the pointer to <paramref name="point"/> of <paramref name="top"/> (device independent).</summary>
    public static void To(TopLevel top, Point point)
    {
        try
        {
            var handle = top.TryGetPlatformHandle();
            switch (handle?.HandleDescriptor)
            {
                case "HWND":
                    // The top level's origin is the client area's; Windows works in physical pixels
                    var p = new WinPoint { X = (int)Math.Round(point.X * top.RenderScaling), Y = (int)Math.Round(point.Y * top.RenderScaling) };
                    if (ClientToScreen(handle.Handle, ref p))
                        SetCursorPos(p.X, p.Y);
                    break;
                case "XID":
                    if (Display() is var display && display != IntPtr.Zero)
                    {
                        // Relative to the window, in physical pixels
                        XWarpPointer(display, IntPtr.Zero, handle.Handle, 0, 0, 0, 0,
                            (int)Math.Round(point.X * top.RenderScaling), (int)Math.Round(point.Y * top.RenderScaling));
                        XFlush(display);
                    }
                    break;
                case "NSWindow" or "NSView":
                    // macOS places windows in points, from the top left of the main screen, as
                    // CoreGraphics does
                    var screen = top.PointToScreen(point);
                    CGWarpMouseCursorPosition(new CGPoint { X = screen.X, Y = screen.Y });
                    // Without this, the mouse is still for a quarter of a second after a warp
                    CGAssociateMouseAndMouseCursorPosition(1);
                    break;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // The pointer stays; the scripts still see the position they set
        }
    }

    /// <summary>A connection of our own to the X server: window ids are the server's, so any connection can use them.</summary>
    private static IntPtr Display()
    {
        if (s_display == IntPtr.Zero && !s_noDisplay)
        {
            s_display = XOpenDisplay(IntPtr.Zero);
            s_noDisplay = s_display == IntPtr.Zero;
        }
        return s_display;
    }

    private struct WinPoint
    {
        public int X, Y;
    }

    private struct CGPoint
    {
        public double X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref WinPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XWarpPointer(IntPtr display, IntPtr source, IntPtr destination,
        int sourceX, int sourceY, uint sourceWidth, uint sourceHeight, int x, int y);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGWarpMouseCursorPosition(CGPoint point);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGAssociateMouseAndMouseCursorPosition(int connected);
}
