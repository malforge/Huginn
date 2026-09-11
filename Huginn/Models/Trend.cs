using System;
using System.Collections.Generic;
using System.Linq;

namespace Huginn.Models;

/// <summary>
/// Turns a bucketed series into a glance: a sparkline, and a sentence saying whether the thing
/// is new. A measurement alone cannot distinguish a problem that started an hour ago from one
/// that has always been there, which is the first question worth asking about either.
/// </summary>
public static class Trend
{
    private const string Blocks = "▁▂▃▄▅▆▇█";

    /// <summary>Renders the series as block characters, scaled to its own range.</summary>
    public static string Spark(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return "";

        double high = values.Max();

        // Scaled from zero rather than from the lowest bucket. Scaling from the minimum makes
        // every series span the full height, so a single spike flattens everything around it
        // and a steady-but-bad level looks identical to a quiet one.
        if (high <= 0) return new string(Blocks[0], values.Count);

        return string.Concat(values.Select(v =>
            Blocks[(int)Math.Clamp(Math.Round(v / high * (Blocks.Length - 1)), 0, Blocks.Length - 1)]));
    }

    /// <summary>
    /// Describes the shape in one phrase. Compares the recent half against the earlier half,
    /// because a step change is what separates "this is new" from "this is how it always is".
    /// </summary>
    public static string Describe(IReadOnlyList<double> values, int bucketMinutes)
    {
        if (values.Count < 4) return "";

        int half = values.Count / 2;
        double before = Median(values.Take(half));
        double after = Median(values.Skip(half));
        double latest = values[^1];

        // Everything is relative to the earlier half, so a quiet period does not read as a crisis.
        if (before <= 0 && after <= 0) return "flat";

        if (before <= 0) return "started this window";

        double ratio = after / before;

        if (ratio >= 2)
        {
            int at = FirstSustainedRise(values, before);
            int minutesAgo = (values.Count - at) * bucketMinutes;
            return at > 0 ? $"stepped up {Humanise(minutesAgo)} ago" : "stepped up this window";
        }

        if (ratio <= 0.5) return "improving";
        if (latest >= before * 2) return "spiking now";

        return "steady all window";
    }

    /// <summary>First bucket from which the series stays meaningfully above where it was.</summary>
    private static int FirstSustainedRise(IReadOnlyList<double> values, double baseline)
    {
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] < baseline * 1.5) continue;
            if (values.Skip(i).Count(v => v >= baseline * 1.5) >= (values.Count - i) * 0.6) return i;
        }

        return 0;
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = [.. values.OrderBy(v => v)];
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Humanise(int minutes) =>
        minutes >= 1440 ? $"{minutes / 1440}d"
        : minutes >= 60 ? $"{minutes / 60}h"
        : $"{minutes}m";
}
