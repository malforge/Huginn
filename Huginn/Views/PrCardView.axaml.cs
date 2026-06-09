using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Huginn.Views;

public partial class PrCardView : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> AcknowledgeCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(AcknowledgeCommand));

    public static readonly StyledProperty<ICommand?> CopyLinkCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(CopyLinkCommand));

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ICommand? AcknowledgeCommand
    {
        get => GetValue(AcknowledgeCommandProperty);
        set => SetValue(AcknowledgeCommandProperty, value);
    }

    public ICommand? CopyLinkCommand
    {
        get => GetValue(CopyLinkCommandProperty);
        set => SetValue(CopyLinkCommandProperty, value);
    }

    public PrCardView()
    {
        InitializeComponent();
    }
}
