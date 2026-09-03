using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using QuickJack.App.Interop;

namespace QuickJack.App.Services;

/// <summary>
/// A single system-wide hot key that summons the palette from whatever app has focus.
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int HotKeyId = 0xB0B;

    private HwndSource? _source;
    private nint _hwnd;
    private bool _registered;

    public event EventHandler? Pressed;

    /// <summary>Set when registration failed, e.g. another app already owns the combination.</summary>
    public string? LastError { get; private set; }

    public bool IsRegistered => _registered;

    /// <summary>
    /// Combinations tried when the configured one is already owned by another app.
    /// Ctrl+Alt+Space in particular is often taken (IME and emoji pickers claim it), and a
    /// hot key that silently does nothing with no way to change it is a dead end.
    /// </summary>
    private static readonly string[] Fallbacks =
        ["Ctrl+Alt+Space", "Ctrl+Shift+Space", "Ctrl+Alt+J", "Ctrl+Shift+J", "Win+J"];

    /// <summary>The combination actually registered, which may not be the one requested.</summary>
    public string? ActiveGesture { get; private set; }

    public bool Attach(Window window, string gesture)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        if (Register(gesture)) return true;

        foreach (var fallback in Fallbacks)
        {
            if (fallback == gesture) continue;
            if (!Register(fallback)) continue;

            LastError = $"{gesture} was already in use, so QuickJack is using {fallback} instead.";
            return true;
        }

        LastError = $"{gesture} is in use and no fallback was free. Set \"hotkey\" in settings.json.";
        return false;
    }

    public bool Register(string gesture)
    {
        Unregister();

        if (!TryParse(gesture, out var modifiers, out var virtualKey))
        {
            LastError = $"'{gesture}' is not a hot key combination QuickJack understands.";
            return false;
        }

        // NoRepeat: holding the keys down should summon the palette once, not repeatedly.
        _registered = Native.RegisterHotKey(
            _hwnd, HotKeyId, (uint)(modifiers | Native.HotKeyModifiers.NoRepeat), virtualKey);

        if (_registered) ActiveGesture = gesture;

        // Silent failure here would be the worst outcome — the user presses the key, nothing
        // happens, and nothing explains why.
        LastError = _registered
            ? null
            : $"{gesture} is already in use by another application. Pick a different combination.";

        return _registered;
    }

    private void Unregister()
    {
        if (!_registered || _hwnd == 0) return;

        Native.UnregisterHotKey(_hwnd, HotKeyId);
        _registered = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Native.WmHotKey && wParam == HotKeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return 0;
    }

    /// <summary>Parses "Ctrl+Alt+Space" into RegisterHotKey's modifier flags and virtual key.</summary>
    internal static bool TryParse(string gesture, out Native.HotKeyModifiers modifiers, out uint virtualKey)
    {
        modifiers = Native.HotKeyModifiers.None;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.HotKeyModifiers.Control; break;
                case "alt": modifiers |= Native.HotKeyModifiers.Alt; break;
                case "shift": modifiers |= Native.HotKeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= Native.HotKeyModifiers.Win; break;
                default: return false;
            }
        }

        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key)) return false;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;

        virtualKey = (uint)vk;

        // A bare key would hijack that key everywhere on the system.
        return modifiers != Native.HotKeyModifiers.None;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
