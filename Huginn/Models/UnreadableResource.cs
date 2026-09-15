namespace Huginn.Models;

/// <summary>A watched resource Huginn could not read this poll, and why.</summary>
/// <remarks>
/// Recorded rather than only logged. A resource that cannot be examined contributes no findings,
/// and without this the pane shows what was readable as though it were the whole picture: the
/// failure looks like good news.
/// </remarks>
/// <param name="Name">The resource as the user picked it.</param>
/// <param name="Reason">What went wrong, in the words the caller can act on.</param>
/// <param name="NeedsSignIn">The sign-in has lapsed, so re-authenticating is the fix.</param>
public sealed record UnreadableResource(string Name, string Reason, bool NeedsSignIn);
