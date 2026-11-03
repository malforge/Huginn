using CommunityToolkit.Mvvm.ComponentModel;

namespace Huginn.Models;

/// <summary>Live health of one data source, surfaced in the settings list and the warning banner.</summary>
public sealed partial class ConnectionStatus : ObservableObject
{
    public ConnectionKind Kind { get; init; }

    /// <summary>Name shown to the user, e.g. "Azure DevOps".</summary>
    public string Title { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private ConnectionState _state = ConnectionState.NotConfigured;

    /// <summary>
    /// Turned on in settings. A source turned off reports nothing and asks for nothing, whatever
    /// state it was last left in.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    private bool _isEnabled = true;

    /// <summary>Detail replacing the default state text, e.g. an error from the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string _message = "";

    /// <summary>The user must do something before this source can report.</summary>
    public bool NeedsAttention =>
        IsEnabled && State is ConnectionState.NotConfigured or ConnectionState.AuthFailed or ConnectionState.Error;

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>A check is in flight, so actions against this connection are disabled.</summary>
    public bool IsBusy => State == ConnectionState.Connecting;

    public string Glyph => !IsEnabled ? "⊘" : State switch
    {
        ConnectionState.Connected => "✅",
        ConnectionState.Connecting => "⋯",
        ConnectionState.AuthFailed => "⚠️",
        ConnectionState.Error => "❌",
        _ => "○",
    };

    public string StateText => !IsEnabled ? "Turned off"
        : !string.IsNullOrEmpty(Message)
        ? Message
        : State switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.AuthFailed => "Sign-in required",
            ConnectionState.Error => "Error",
            _ => "Not configured",
        };

    /// <summary>Moves the connection to a new state, clearing any stale detail.</summary>
    public void Set(ConnectionState state, string message = "")
    {
        State = state;
        Message = message;
    }
}
