using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Huginn.Views;

public partial class BuildCardView : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<BuildCardView, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> OpenRetryCommandProperty =
        AvaloniaProperty.Register<BuildCardView, ICommand?>(nameof(OpenRetryCommand));

    public static readonly StyledProperty<ICommand?> AcknowledgeCommandProperty =
        AvaloniaProperty.Register<BuildCardView, ICommand?>(nameof(AcknowledgeCommand));

    public static readonly StyledProperty<ICommand?> CopyLinkCommandProperty =
        AvaloniaProperty.Register<BuildCardView, ICommand?>(nameof(CopyLinkCommand));

    public static readonly StyledProperty<ICommand?> CopyBranchCommandProperty =
        AvaloniaProperty.Register<BuildCardView, ICommand?>(nameof(CopyBranchCommand));

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ICommand? OpenRetryCommand
    {
        get => GetValue(OpenRetryCommandProperty);
        set => SetValue(OpenRetryCommandProperty, value);
    }

    public ICommand? AcknowledgeCommand
    {
        get => GetValue(AcknowledgeCommandProperty);
        set => SetValue(AcknowledgeCommandProperty, value);
    }

    /// <summary>Copies the link to the run, for handing the failure to someone else.</summary>
    public ICommand? CopyLinkCommand
    {
        get => GetValue(CopyLinkCommandProperty);
        set => SetValue(CopyLinkCommandProperty, value);
    }

    /// <summary>Copies the branch the build ran on, which is usually the next thing to check out.</summary>
    public ICommand? CopyBranchCommand
    {
        get => GetValue(CopyBranchCommandProperty);
        set => SetValue(CopyBranchCommandProperty, value);
    }

    public BuildCardView()
    {
        InitializeComponent();

        // An open menu talks about "this one", so the card it belongs to marks itself while it
        // is open. In a list of near-identical rows there is otherwise nothing saying which.
        if (MoreButton.Flyout is not { } flyout) return;

        flyout.Opened += (_, _) => Card.Classes.Add("menu-open");
        flyout.Closed += (_, _) => Card.Classes.Remove("menu-open");
    }
}
