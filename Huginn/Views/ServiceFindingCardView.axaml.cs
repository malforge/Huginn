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
    }
}
