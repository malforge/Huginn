namespace Huginn.Models;

/// <summary>One fixed question Huginn can ask about a finding, and the query that answers it.</summary>
/// <param name="Title">What the answer is, e.g. "Result codes".</param>
/// <param name="Kql">The query, written here rather than composed by a caller.</param>
public sealed record Drilldown(string Title, string Kql);
