using System.Text.RegularExpressions;

namespace Huginn.Models;

/// <summary>
/// Folds the names requests are recorded under into the route they were made against, as KQL.
/// </summary>
/// <remarks>
/// A server that records the path rather than its template names every call by the id it was
/// made with, so one route arrives as thousands of names, each too thin to judge, and the same
/// route spelled in another case or with a trailing slash arrives as a second finding nobody can
/// tell apart from the first. Ids become <c>{id}</c>: a guid, with or without dashes, or a run of
/// digits filling a whole segment. Case and a trailing slash are folded only in
/// <see cref="Key"/>, so a route keeps the spelling it was called by.
/// </remarks>
public static partial class RouteName
{
    /// <summary>A whole path segment that is an id, followed by what ends the segment.</summary>
    private const string IdSegment =
        @"@""/([0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}|[0-9]+)(/|$)""";

    private const string AsPlaceholder = @"@""/{id}\2""";

    /// <summary>
    /// The name as a template. Applied twice because a match consumes the slash that ends its
    /// segment, so the second of two adjacent ids only matches on the next pass.
    /// </summary>
    public static string Template(string column) =>
        $"replace_regex(replace_regex({column}, {IdSegment}, {AsPlaceholder}), {IdSegment}, {AsPlaceholder})";

    /// <summary>
    /// What two names have in common when they are the same route: the template without its case
    /// or a trailing slash. A bare root keeps its slash.
    /// </summary>
    public static string Key(string column) =>
        $"""tolower(replace_regex({Template(column)}, @"([^ /])/+$", @"\1"))""";

    /// <summary>The key of a subject Huginn already reported, as KQL.</summary>
    public static string KeyOf(string subject) => Key(Literal(subject));

    /// <summary>
    /// The key of a subject Huginn already reported, computed here rather than in a query. The
    /// subject is a template already, so only the folding of <see cref="Key"/> is left to do.
    /// </summary>
    public static string KeyOfTemplate(string subject) =>
        TrailingSlash().Replace(subject, "$1").ToLowerInvariant();

    [GeneratedRegex(@"([^ /])/+$")]
    private static partial Regex TrailingSlash();

    /// <summary>
    /// Wraps a value as a KQL string. Operation names come back from Azure rather than from us,
    /// so they are not assumed to be free of quotes.
    /// </summary>
    public static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
