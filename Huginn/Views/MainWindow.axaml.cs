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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        RestoreWindowState();

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
        (DataContext as IDisposable)?.Dispose();
        base.OnClosing(e);
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
            vm.RefreshCommand.Execute(null);
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