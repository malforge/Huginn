using System;
using System.Collections.Generic;

namespace Huginn.Models;

/// <summary>One result table from an Application Insights query.</summary>
/// <remarks>
/// Cells are held as plain strings rather than JsonElement. A JsonElement is only a view into
/// the JsonDocument that produced it, so returning one outlives the document and throws on
/// first read.
/// </remarks>
/// <param name="Columns">Column names, in the order the rows use.</param>
/// <param name="Rows">Row values, aligned to <paramref name="Columns"/>.</param>
public sealed record QueryTable(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>Index of a column by name, or -1 when the query did not return it.</summary>
    public int IndexOf(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}
