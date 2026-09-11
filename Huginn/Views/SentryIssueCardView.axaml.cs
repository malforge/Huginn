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

    public SentryIssueCardView()
    {
        InitializeComponent();
    }
}
