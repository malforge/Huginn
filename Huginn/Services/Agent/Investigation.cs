using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Huginn.Models;

namespace Huginn.Services.Agent;

/// <summary>
/// Answers "why is this one failing" for a finding, by running the queries that belong to its
/// kind and writing the results out as text.
/// </summary>
/// <remarks>
/// The queries come from <see cref="FindingQuery"/> and nothing else: an agent chooses which
/// finding to look at, never what to ask about it. Exception messages are server-authored and
/// are passed through as they come, so a server that puts an identity in one puts it here too.
/// </remarks>
public static class Investigation
{
    /// <summary>Beyond this a result is summary enough for a person to take over.</summary>
    private const int MaxRows = 40;

    /// <summary>Long enough for an exception message to be recognised, short enough to scan.</summary>
    private const int MaxCellLength = 160;

    /// <summary>Runs every drill-down for the finding and returns them as one report.</summary>
    public static async Task<string> RunAsync(
        ServiceFinding finding, AppInsightsApiClient client, CancellationToken ct)
    {
        StringBuilder report = new();
        report.AppendLine($"{finding.KindLabel}: {finding.Subject}");
        report.AppendLine($"On {finding.ResourceName}, over the last {finding.WindowMinutes}m.");
        report.AppendLine(finding.Detail);

        foreach (Drilldown drilldown in FindingQuery.Explanations(finding))
        {
            report.AppendLine();
            report.AppendLine(drilldown.Title.ToUpperInvariant());

            try
            {
                QueryTable table = await client.QueryAsync(finding.AppId, drilldown.Kql, ct);
                report.AppendLine(Render(table));
            }
            catch (Exception ex)
            {
                // One query failing says nothing about the next, so carry on and report it.
                report.AppendLine($"  Could not run this one: {ex.Message}");
            }
        }

        return report.ToString().TrimEnd();
    }

    /// <summary>Lays a result table out in columns, so a row reads without counting commas.</summary>
    private static string Render(QueryTable table)
    {
        if (table.Rows.Count == 0) return "  (no rows)";

        List<IReadOnlyList<string>> rows =
            [.. table.Rows.Take(MaxRows).Select(r => (IReadOnlyList<string>)[.. r.Select(Trim)])];

        int[] widths = [.. table.Columns.Select((c, i) =>
            Math.Max(c.Length, rows.Max(r => i < r.Count ? r[i].Length : 0)))];

        StringBuilder text = new();
        text.AppendLine("  " + Line(table.Columns, widths));
        text.AppendLine("  " + Line([.. widths.Select(w => new string('-', w))], widths));

        foreach (IReadOnlyList<string> row in rows)
            text.AppendLine("  " + Line(row, widths));

        if (table.Rows.Count > rows.Count)
            text.AppendLine($"  ... {table.Rows.Count - rows.Count} more rows");

        return text.ToString().TrimEnd();
    }

    private static string Line(IReadOnlyList<string> cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => c.PadRight(i < widths.Length ? widths[i] : 0)))
              .TrimEnd();

    private static string Trim(string cell)
    {
        string flat = cell.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= MaxCellLength ? flat : flat[..MaxCellLength] + "...";
    }
}
