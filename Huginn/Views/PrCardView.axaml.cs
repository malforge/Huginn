using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Huginn.Views;

public partial class PrCardView : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> AcknowledgeCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(AcknowledgeCommand));

    public static readonly StyledProperty<ICommand?> CopyLinkCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(CopyLinkCommand));

    /// <summary>Why this card was raised, e.g. "No reviewers assigned". Hidden when empty.</summary>
    public static readonly StyledProperty<string?> AlertNoteProperty =
        AvaloniaProperty.Register<PrCardView, string?>(nameof(AlertNote));

    /// <summary>Colour for <see cref="AlertNote"/>, so each section keeps its own severity.</summary>
    public static readonly StyledProperty<IBrush?> AlertBrushProperty =
        AvaloniaProperty.Register<PrCardView, IBrush?>(nameof(AlertBrush));

    public string? AlertNote
    {
        get => GetValue(AlertNoteProperty);
        set => SetValue(AlertNoteProperty, value);
    }

    public IBrush? AlertBrush
    {
        get => GetValue(AlertBrushProperty);
        set => SetValue(AlertBrushProperty, value);
    }

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
