namespace Huginn.Models;

/// <summary>
/// Records how big an issue was when the user muted it, so Huginn can tell later whether it has
/// grown enough to be worth raising again.
/// </summary>
public sealed class SentryAcknowledgement
{
    public int UserCount { get; set; }

    public int EventCount { get; set; }
}
