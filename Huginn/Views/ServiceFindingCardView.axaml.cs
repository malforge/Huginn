using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Huginn.Views;

public partial class ServiceFindingCardView : UserControl
{
    public static readonly StyledProperty<ICommand?> MuteCommandProperty =
        AvaloniaProperty.Register<ServiceFindingCardView, ICommand?>(nameof(MuteCommand));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<ServiceFindingCardView, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<ServiceFindingCardView, ICommand?>(nameof(DismissCommand));

    public static readonly StyledProperty<ICommand?> IgnoreCommandProperty =
        AvaloniaProperty.Register<ServiceFindingCardView, ICommand?>(nameof(IgnoreCommand));

    public static readonly StyledProperty<ICommand?> CopyQueryCommandProperty =
        AvaloniaProperty.Register<ServiceFindingCardView, ICommand?>(nameof(CopyQueryCommand));

    public static readonly StyledProperty<ICommand?> ShowSuppressedCommandProperty =
        AvaloniaProperty.Register<ServiceFindingCardView, ICommand?>(nameof(ShowSuppressedCommand));

    /// <summary>Opens the list of what the rules are keeping off the board.</summary>
    public ICommand? ShowSuppressedCommand
    {
        get => GetValue(ShowSuppressedCommandProperty);
        set => SetValue(ShowSuppressedCommandProperty, value);
    }

    /// <summary>Never show this subject again, as opposed to muting one finding of it.</summary>
    public ICommand? IgnoreCommand
    {
        get => GetValue(IgnoreCommandProperty);
        set => SetValue(IgnoreCommandProperty, value);
    }

    /// <summary>Copies the query that produced this finding, for digging further by hand.</summary>
    public ICommand? CopyQueryCommand
    {
        get => GetValue(CopyQueryCommandProperty);
        set => SetValue(CopyQueryCommandProperty, value);
    }

    public ICommand? MuteCommand
    {
        get => GetValue(MuteCommandProperty);
        set => SetValue(MuteCommandProperty, value);
    }

    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ServiceFindingCardView()
    {
        InitializeComponent();

        // An open menu talks about "this one", so the card it belongs to marks itself while it
        // is open. In a list of near-identical rows there is otherwise nothing saying which.
        if (MoreButton.Flyout is not { } flyout) return;

        flyout.Opened += (_, _) => Card.Classes.Add("menu-open");
        flyout.Closed += (_, _) => Card.Classes.Remove("menu-open");
    }
}
