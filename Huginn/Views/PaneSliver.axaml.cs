using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Huginn.Models;

namespace Huginn.Views;

public partial class PaneSliver : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PaneSliver, string>(nameof(Title), "");

    public static readonly StyledProperty<string> CountProperty =
        AvaloniaProperty.Register<PaneSliver, string>(nameof(Count), "");

    public static readonly StyledProperty<PaneKind> TargetProperty =
        AvaloniaProperty.Register<PaneSliver, PaneKind>(nameof(Target));

    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<PaneSliver, bool>(nameof(IsVertical), true);

    public static readonly StyledProperty<ICommand?> FocusCommandProperty =
        AvaloniaProperty.Register<PaneSliver, ICommand?>(nameof(FocusCommand));

    public static readonly StyledProperty<ICommand?> RestoreCommandProperty =
        AvaloniaProperty.Register<PaneSliver, ICommand?>(nameof(RestoreCommand));

    public PaneSliver()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>How many items the pane holds, or empty for none.</summary>
    public string Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>The pane this sliver stands for, which its click gives focus to.</summary>
    public PaneKind Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Upright beside the focused pane; a strip when the panes are stacked.</summary>
    public bool IsVertical
    {
        get => GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    public ICommand? FocusCommand
    {
        get => GetValue(FocusCommandProperty);
        set => SetValue(FocusCommandProperty, value);
    }

    public ICommand? RestoreCommand
    {
        get => GetValue(RestoreCommandProperty);
        set => SetValue(RestoreCommandProperty, value);
    }
}
