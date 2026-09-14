namespace Huginn.Models;

/// <summary>
/// Something the ignore list kept off the board, and how much of it there was.
/// </summary>
/// <remarks>
/// Recorded so a rule can be shown its own effect. A filter nobody can audit is how a real
/// failure stays hidden: the rule that used to drop every 404 was concealing a route of our own
/// being called thousands of times an hour, and nothing on screen said so.
/// </remarks>
/// <param name="Subject">The operation or dependency target that was suppressed.</param>
/// <param name="Resource">The resource it was seen on.</param>
/// <param name="Pattern">The pattern that matched it.</param>
/// <param name="Calls">How many calls it accounted for in the window.</param>
public sealed record IgnoredSubject(string Subject, string Resource, string Pattern, long Calls);
