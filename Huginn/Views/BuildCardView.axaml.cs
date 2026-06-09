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

    public BuildCardView()
    {
        InitializeComponent();
    }
}
