using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Huginn.Models;
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
        if (DataContext is MainWindowViewModel model) model.PropertyChanged += OnViewModelChanged;
        ArrangePanes();

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
        if (DataContext is MainWindowViewModel model) model.PropertyChanged -= OnViewModelChanged;
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
        ArrangePanes();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.FocusedPane)
            or nameof(MainWindowViewModel.AdoEnabled)
            or nameof(MainWindowViewModel.SentryEnabled)
            or nameof(MainWindowViewModel.AppInsightsEnabled))
        {
            ArrangePanes();
        }
    }

    /// <summary>
    /// Lays the panes out as <see cref="PaneLayout"/> decides: a share each, or most of the room
    /// to the one with focus and a sliver to the rest. Done here rather than in markup because
    /// the number of columns or rows, and which is which, depends on what is shown.
    /// </summary>
    private void ArrangePanes()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        (PaneKind Kind, bool Shown, Panel Slot, Control Pane, PaneSliver Sliver)[] panes =
        [
            (PaneKind.AzureDevOps, vm.AdoEnabled, AdoSlot, AdoPane, AdoSliver),
            (PaneKind.Sentry, vm.SentryEnabled, SentrySlot, SentryPane, SentrySliver),
            (PaneKind.Services, vm.AppInsightsEnabled, ServicesSlot, ServicesPane, ServicesSliver),
        ];

        List<PaneKind> shown = [];
        foreach (var p in panes) if (p.Shown) shown.Add(p.Kind);

        var (stacked, tracks) = PaneLayout.Arrange(shown, vm.FocusedPane, Bounds.Width);

        PaneHost.ColumnDefinitions.Clear();
        PaneHost.RowDefinitions.Clear();
        foreach (var p in panes) p.Slot.IsVisible = false;

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var p = Array.Find(panes, x => x.Kind == track.Pane);

            GridLength size = !track.Collapsed ? GridLength.Star
                : stacked ? GridLength.Auto
                : new GridLength(PaneLayout.Sliver);

            if (stacked) PaneHost.RowDefinitions.Add(new RowDefinition(size));
            else PaneHost.ColumnDefinitions.Add(new ColumnDefinition(size));

            Grid.SetRow(p.Slot, stacked ? i : 0);
            Grid.SetColumn(p.Slot, stacked ? 0 : i);
            p.Slot.IsVisible = true;
            p.Pane.IsVisible = !track.Collapsed;
            p.Sliver.IsVisible = track.Collapsed;
            p.Sliver.IsVertical = !stacked;
        }
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