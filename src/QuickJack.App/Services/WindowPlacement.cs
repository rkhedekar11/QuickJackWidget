using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using QuickJack.App.Interop;

namespace QuickJack.App.Services;

/// <summary>
/// Positions the widget in physical screen pixels.
/// <para>
/// WPF's <c>Window.Left</c>/<c>Top</c> are device-independent units whose meaning gets
/// slippery across monitors with different scaling. Placement here goes through
/// <c>SetWindowPos</c> with real pixels, and screen bounds come from WinForms
/// <see cref="Screen"/> in the same units — so no conversion is needed and mixed-DPI setups
/// behave.
/// </para>
/// </summary>
internal static class WindowPlacement
{
    public const int SnapMargin = 12;

    public static nint HandleOf(Window window) => new WindowInteropHelper(window).Handle;

    public static Native.Rect PhysicalBounds(Window window)
    {
        Native.GetWindowRect(HandleOf(window), out var rect);
        return rect;
    }

    public static void MoveTo(Window window, int x, int y) =>
        Native.SetWindowPos(HandleOf(window), 0, x, y, 0, 0,
            Native.SwpNoSize | Native.SwpNoActivate | Native.SwpNoZOrder);

    /// <summary>
    /// Re-asserts topmost. WPF's Topmost alone loses the fight after some full-screen apps
    /// and after a display change, so it is pushed again explicitly.
    /// </summary>
    public static void BringToFront(Window window) =>
        Native.SetWindowPos(HandleOf(window), Native.HwndTopmost, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);

    public static Screen ScreenContaining(int x, int y) =>
        Screen.FromPoint(new System.Drawing.Point(x, y));

    /// <summary>Nudges the window against whichever working-area edge it ended up nearest.</summary>
    public static (int X, int Y) SnapToEdge(Native.Rect window)
    {
        var work = ScreenContaining(window.Left + window.Width / 2, window.Top + window.Height / 2)
            .WorkingArea;

        var left = window.Left - work.Left;
        var right = work.Right - window.Right;
        var top = window.Top - work.Top;
        var bottom = work.Bottom - window.Bottom;

        var x = window.Left;
        var y = window.Top;

        if (Math.Min(left, right) <= Math.Min(top, bottom))
            x = left <= right ? work.Left + SnapMargin : work.Right - window.Width - SnapMargin;
        else
            y = top <= bottom ? work.Top + SnapMargin : work.Bottom - window.Height - SnapMargin;

        return Clamp(x, y, window.Width, window.Height);
    }

    /// <summary>
    /// Keeps the window inside a working area that actually exists. Without this the orb
    /// strands itself offscreen the first time a second monitor is unplugged — the classic
    /// failure for a tool like this.
    /// </summary>
    public static (int X, int Y) Clamp(int x, int y, int width, int height)
    {
        var screen = ScreenContaining(x + width / 2, y + height / 2);
        var work = screen.WorkingArea;

        // FromPoint returns the nearest screen for an off-screen point, so this both clamps
        // within a monitor and rescues a position on a monitor that no longer exists.
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        return (x, y);
    }

    /// <summary>
    /// Places the expanded palette next to the orb, growing towards the middle of the
    /// screen so it never opens off the edge it is snapped to.
    /// </summary>
    public static (int X, int Y) PlacePalette(Native.Rect orb, int width, int height)
    {
        var work = ScreenContaining(orb.Left + orb.Width / 2, orb.Top + orb.Height / 2).WorkingArea;

        var onLeftHalf = orb.Left + orb.Width / 2 < work.Left + work.Width / 2;
        var onTopHalf = orb.Top + orb.Height / 2 < work.Top + work.Height / 2;

        var x = onLeftHalf ? orb.Left : orb.Right - width;
        var y = onTopHalf ? orb.Top : orb.Bottom - height;

        return Clamp(x, y, width, height);
    }

    /// <summary>Adds WS_EX_TOOLWINDOW so the widget stays out of Alt-Tab and the taskbar.</summary>
    public static void MakeToolWindow(Window window)
    {
        var hwnd = HandleOf(window);
        var style = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, style | Native.WsExToolWindow);
    }

}
