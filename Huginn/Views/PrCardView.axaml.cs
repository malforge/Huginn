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

    public static readonly StyledProperty<ICommand?> CopyReferenceCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(CopyReferenceCommand));

    public static readonly StyledProperty<ICommand?> CopyTitleCommandProperty =
        AvaloniaProperty.Register<PrCardView, ICommand?>(nameof(CopyTitleCommand));

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

    /// <summary>Copies the reference form, !12345, rather than the bare number.</summary>
    public ICommand? CopyReferenceCommand
    {
        get => GetValue(CopyReferenceCommandProperty);
        set => SetValue(CopyReferenceCommandProperty, value);
    }

    /// <summary>Copies the title, for a release note or a ticket.</summary>
    public ICommand? CopyTitleCommand
    {
        get => GetValue(CopyTitleCommandProperty);
        set => SetValue(CopyTitleCommandProperty, value);
    }

    public PrCardView()
    {
        InitializeComponent();

        // An open menu talks about "this one", so the card it belongs to marks itself while it
        // is open. In a list of near-identical rows there is otherwise nothing saying which.
        if (MoreButton.Flyout is not { } flyout) return;

        flyout.Opened += (_, _) => Card.Classes.Add("menu-open");
        flyout.Closed += (_, _) => Card.Classes.Remove("menu-open");
    }
}
