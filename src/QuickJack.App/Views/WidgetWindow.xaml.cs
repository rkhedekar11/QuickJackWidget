using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using QuickJack.App.Interop;
using QuickJack.App.Services;
using QuickJack.App.ViewModels;

namespace QuickJack.App.Views;

public partial class WidgetWindow : Window
{
    private const double OrbSize = 56;
    private const double PaletteWidth = 400;
    private const double PaletteHeight = 520;

    /// <summary>Movement beyond this many pixels turns a click into a drag.</summary>
    private const int DragThreshold = 4;

    private readonly SettingsStore _settings;
    private readonly WidgetViewModel _viewModel;

    private bool _dragging;
    private bool _movedWhileDown;
    private Native.Point _dragOrigin;
    private Native.Rect _windowAtDragStart;

    public WidgetWindow(WidgetViewModel viewModel, SettingsStore settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        Orb.MouseLeftButtonDown += OnDragStart;
        Orb.MouseMove += OnDragMove;
        Orb.MouseLeftButtonUp += OnDragEnd;

        DragHandle.MouseLeftButtonDown += OnDragStart;
        DragHandle.MouseMove += OnDragMove;
        DragHandle.MouseLeftButtonUp += OnDragEnd;

        Deactivated += (_, _) => { if (IsExpanded) Collapse(); };
        PreviewKeyDown += OnPreviewKeyDown;

        // The orb must come back onto a monitor that still exists when one is unplugged or
        // the resolution changes; otherwise it strands itself offscreen for good.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool IsExpanded { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowPlacement.MakeToolWindow(this);

        RestorePosition();
        WindowPlacement.BringToFront(this);
    }

    // ---- position ----

    private void RestorePosition()
    {
        var bounds = WindowPlacement.PhysicalBounds(this);
        var width = bounds.Width > 0 ? bounds.Width : (int)OrbSize;
        var height = bounds.Height > 0 ? bounds.Height : (int)OrbSize;

        var saved = _settings.Current;
        int x, y;

        if (saved.OrbX is { } sx && saved.OrbY is { } sy)
        {
            (x, y) = WindowPlacement.Clamp(sx, sy, width, height);
        }
        else
        {
            // First run: bottom-right of the primary working area, out of the way.
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            x = work.Right - width - 32;
            y = work.Bottom - height - 32;
        }

        WindowPlacement.MoveTo(this, x, y);
        SavePosition(x, y);
    }

    private void SavePosition(int x, int y) =>
        _settings.Update(s => { s.OrbX = x; s.OrbY = y; });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            var bounds = WindowPlacement.PhysicalBounds(this);
            var (x, y) = WindowPlacement.Clamp(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            WindowPlacement.MoveTo(this, x, y);
            WindowPlacement.BringToFront(this);
            if (!IsExpanded) SavePosition(x, y);
        });

    // ---- dragging ----

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or ButtonBase) return;

        Native.GetCursorPos(out _dragOrigin);
        _windowAtDragStart = WindowPlacement.PhysicalBounds(this);
        _dragging = true;
        _movedWhileDown = false;

        ((UIElement)sender).CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Native.GetCursorPos(out var cursor);
        var dx = cursor.X - _dragOrigin.X;
        var dy = cursor.Y - _dragOrigin.Y;

        if (!_movedWhileDown && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;

        _movedWhileDown = true;
        WindowPlacement.MoveTo(this, _windowAtDragStart.Left + dx, _windowAtDragStart.Top + dy);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();

        if (!_movedWhileDown)
        {
            // A press that never moved is a click, not a zero-distance drag.
            if (!IsExpanded) Expand();
            return;
        }

        if (IsExpanded) return;

        var bounds = WindowPlacement.PhysicalBounds(this);
        var (x, y) = WindowPlacement.SnapToEdge(bounds);
        WindowPlacement.MoveTo(this, x, y);
        SavePosition(x, y);
    }

    // ---- expand / collapse ----

    public void Toggle()
    {
        if (IsExpanded) Collapse();
        else Expand();
    }

    public void Expand()
    {
        if (IsExpanded) return;

        var orb = WindowPlacement.PhysicalBounds(this);
        _settings.Update(s => { s.OrbX = orb.Left; s.OrbY = orb.Top; });

        IsExpanded = true;
        Orb.Visibility = Visibility.Collapsed;
        Palette.Visibility = Visibility.Visible;
        Width = PaletteWidth;
        Height = PaletteHeight;
        UpdateLayout();

        // Size is only known in physical pixels once WPF has laid out at this monitor's DPI.
        var palette = WindowPlacement.PhysicalBounds(this);
        var (x, y) = WindowPlacement.PlacePalette(orb, palette.Width, palette.Height);
        WindowPlacement.MoveTo(this, x, y);

        _viewModel.SearchText = string.Empty;

        // Reopening should not throw away a run the user is waiting on - notably one the
        // API started while the palette was closed.
        if (_viewModel.CurrentRun?.IsRunning != true) _viewModel.Back();
        _viewModel.Refresh();

        WindowPlacement.BringToFront(this);
        Show();
        Activate();

        // A borderless topmost window is unreliable about taking keyboard focus; the
        // explicit SetForegroundWindow plus a deferred focus is what makes typing work.
        Native.SetForegroundWindow(WindowPlacement.HandleOf(this));
        Dispatcher.InvokeAsync(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }, System.Windows.Threading.DispatcherPriority.Input);

        PlayFade();
    }

    public void Collapse()
    {
        if (!IsExpanded) return;

        IsExpanded = false;
        Palette.Visibility = Visibility.Collapsed;
        Orb.Visibility = Visibility.Visible;
        Width = OrbSize;
        Height = OrbSize;
        UpdateLayout();

        var saved = _settings.Current;
        var bounds = WindowPlacement.PhysicalBounds(this);
        var (x, y) = WindowPlacement.Clamp(
            saved.OrbX ?? bounds.Left, saved.OrbY ?? bounds.Top, bounds.Width, bounds.Height);

        WindowPlacement.MoveTo(this, x, y);
        WindowPlacement.BringToFront(this);
    }

    private void PlayFade()
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110));
        Palette.BeginAnimation(OpacityProperty, fade);
    }

    // ---- input ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsExpanded) return;

        switch (e.Key)
        {
            case Key.Escape:
                if (_viewModel.Mode == PaletteMode.Browsing) Collapse();
                else _viewModel.Back();
                e.Handled = true;
                break;

            case Key.Down when _viewModel.Mode == PaletteMode.Browsing:
                _viewModel.MoveSelection(1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;

            case Key.Up when _viewModel.Mode == PaletteMode.Browsing:
                _viewModel.MoveSelection(-1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;

            case Key.Enter when _viewModel.Mode == PaletteMode.Browsing:
                _viewModel.ActivateCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter when _viewModel.Mode == PaletteMode.CollectingParameters:
                _viewModel.SubmitParametersCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectionIntoView()
    {
        if (_viewModel.Selected is not null) CommandList.ScrollIntoView(_viewModel.Selected);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) =>
        _viewModel.ActivateCommand.Execute(null);

    private void OnCollapseClick(object sender, RoutedEventArgs e) => Collapse();

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }
}
