using System.Collections.Generic;
using System.Text.Json;

namespace Huginn.Models;

/// <summary>One result table from an Application Insights query.</summary>
/// <param name="Columns">Column names, in the order the rows use.</param>
/// <param name="Rows">Row values, aligned to <paramref name="Columns"/>.</param>
public sealed record QueryTable(IReadOnlyList<string> Columns, IReadOnlyList<List<JsonElement>> Rows)
{
    /// <summary>Index of a column by name, or -1 when the query did not return it.</summary>
    public int IndexOf(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], column, System.StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
