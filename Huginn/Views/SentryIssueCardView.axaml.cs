using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Huginn.Views;

public partial class SentryIssueCardView : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<SentryIssueCardView, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> MuteCommandProperty =
        AvaloniaProperty.Register<SentryIssueCardView, ICommand?>(nameof(MuteCommand));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<SentryIssueCardView, ICommand?>(nameof(DismissCommand));

    public static readonly StyledProperty<ICommand?> CopyLinkCommandProperty =
        AvaloniaProperty.Register<SentryIssueCardView, ICommand?>(nameof(CopyLinkCommand));

    public static readonly StyledProperty<ICommand?> CopyIdCommandProperty =
        AvaloniaProperty.Register<SentryIssueCardView, ICommand?>(nameof(CopyIdCommand));

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
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

    /// <summary>Copies the permalink, for handing the crash to someone else.</summary>
    public ICommand? CopyLinkCommand
    {
        get => GetValue(CopyLinkCommandProperty);
        set => SetValue(CopyLinkCommandProperty, value);
    }

    /// <summary>Copies the short id, which is what a ticket or a chat message wants.</summary>
    public ICommand? CopyIdCommand
    {
        get => GetValue(CopyIdCommandProperty);
        set => SetValue(CopyIdCommandProperty, value);
    }

    public SentryIssueCardView()
    {
        InitializeComponent();

        // An open menu talks about "this one", so the card it belongs to marks itself while it
        // is open. In a list of near-identical rows there is otherwise nothing saying which.
        if (MoreButton.Flyout is not { } flyout) return;

        flyout.Opened += (_, _) => Card.Classes.Add("menu-open");
        flyout.Closed += (_, _) => Card.Classes.Remove("menu-open");
    }
}
