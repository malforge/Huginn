using System;
using Avalonia;
using Avalonia.Controls;
using Huginn.Services;
using Huginn.ViewModels;

namespace Huginn.Views;

public partial class MainWindow : Window
{
    private bool _wasDeactivated;

    public MainWindow()
    {
        InitializeComponent();
    }

    private AppSettings? Settings => (DataContext as MainWindowViewModel)?.Settings;

    /// <summary>Width each pane needs before another column is worth having.</summary>
    private const double PaneWidth = 420;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        RestoreWindowState();
        UpdateLayoutColumns();

        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && DataContext is MainWindowViewModel vm)
            vm.InitializeTaskbarBadge(hwnd);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Activated -= OnWindowActivated;
        Deactivated -= OnWindowDeactivated;
        SaveWindowState();

        // Settings are committed on close rather than on an explicit save, so closing the window
        // with the panel open must not throw the edits away either.
        (DataContext as MainWindowViewModel)?.CommitSettings();

        (DataContext as IDisposable)?.Dispose();
        base.OnClosing(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateLayoutColumns();
    }

    private void UpdateLayoutColumns()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Three panes now, since pull requests and builds share one. Only 1 or 3: two columns
        // would leave the third orphaned on a row of its own, which looks like a mistake.
        var fits = (int)(Bounds.Width / PaneWidth);
        vm.LayoutColumns = fits >= 3 ? 3 : 1;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _wasDeactivated = true;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_wasDeactivated && DataContext is MainWindowViewModel vm && vm.IsConnected)
        {
            _wasDeactivated = false;
            _ = vm.RefreshOnFocusAsync();
        }
    }

    private void RestoreWindowState()
    {
        var settings = Settings;
        if (settings == null) return;

        if (settings.WindowWidth is > 0 && settings.WindowHeight is > 0)
        {
            Width = settings.WindowWidth.Value;
            Height = settings.WindowHeight.Value;
        }

        if (settings.WindowX.HasValue && settings.WindowY.HasValue)
        {
            var pos = new PixelPoint((int)settings.WindowX.Value, (int)settings.WindowY.Value);

            if (IsPositionOnScreen(pos))
                Position = pos;
        }

        WindowState = (WindowState)settings.WindowState;
    }

    private bool IsPositionOnScreen(PixelPoint pos)
    {
        var screens = Screens;
        if (screens == null) return true;

        foreach (var screen in screens.All)
        {
            var bounds = screen.WorkingArea;
            if (pos.X >= bounds.X - 100 && pos.X < bounds.X + bounds.Width &&
                pos.Y >= bounds.Y - 50 && pos.Y < bounds.Y + bounds.Height)
                return true;
        }

        return false;
    }

    private void SaveWindowState()
    {
        var settings = Settings;
        if (settings == null) return;

        settings.WindowState = (int)WindowState;

        if (WindowState == WindowState.Normal)
        {
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
        }

        settings.Save();
    }
}