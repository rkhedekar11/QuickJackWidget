using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace QuickJack.App.Services;

/// <summary>
/// Notification-area presence. WPF has no tray API, and the in-box WinForms
/// <see cref="NotifyIcon"/> beats taking a third-party dependency for one icon.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _generated;

    public TrayIcon()
    {
        _generated = IconFactory.CreateBolt();

        _icon = new NotifyIcon
        {
            Icon = _generated,
            Text = "QuickJack",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        _icon.ContextMenuStrip.Items.Add("Show widget", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip.Items.Add("Settings", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;

    public void Notify(string title, string message, ToolTipIcon kind = ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = kind;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generated.Dispose();
    }
}

/// <summary>
/// Draws the tray icon at runtime rather than shipping a .ico, so there is no binary asset
/// to keep in sync and it renders correctly at any DPI.
/// </summary>
internal static class IconFactory
{
    public static Icon CreateBolt(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 32, 34, 40));
            g.FillEllipse(background, 0, 0, size - 1, size - 1);

            using var ring = new Pen(Color.FromArgb(255, 96, 165, 250), size / 16f);
            g.DrawEllipse(ring, 1, 1, size - 3, size - 3);

            var s = size / 32f;
            var bolt = new[]
            {
                new PointF(18 * s, 6 * s),
                new PointF(11 * s, 17 * s),
                new PointF(15.5f * s, 17 * s),
                new PointF(13 * s, 26 * s),
                new PointF(21 * s, 14 * s),
                new PointF(16.5f * s, 14 * s),
            };

            using var boltBrush = new SolidBrush(Color.FromArgb(255, 250, 204, 21));
            g.FillPolygon(boltBrush, bolt);
        }

        // Icon.FromHandle does not own the handle, so the bitmap must be cloned into a
        // standalone Icon before the HICON is destroyed.
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeIcon.DestroyIcon(handle);
        }
    }
}

internal static partial class NativeIcon
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint handle);
}
