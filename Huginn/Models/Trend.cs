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

    /// <summary>
    /// Describes a series of event counts. Separate from <see cref="Describe"/> because that one
    /// compares medians, and a crash that fires in one hour out of twenty-four has a median of
    /// zero on both sides however large the burst was.
    /// </summary>
    /// <param name="values">Events per bucket.</param>
    /// <param name="bucketMinutes">How long one bucket covers.</param>
    public static string DescribeCounts(IReadOnlyList<double> values, int bucketMinutes)
    {
        if (values.Count < 4) return "";

        double total = values.Sum();

        // Below this there is no shape to read, and a caption would only dress a guess up as one.
        if (total < MinEventsToDescribe) return "";

        int active = values.Count(v => v > 0);

        // Concentration first: whether the events arrived in one lump or as a steady drip is the
        // distinction an event total on its own can never make, and the two want different responses.
        if (active <= 2)
        {
            int last = values.Count - 1;
            while (last > 0 && values[last] <= 0) last--;

            int minutesAgo = (values.Count - 1 - last) * bucketMinutes;
            return minutesAgo == 0 ? "one burst, still going" : $"one burst {Humanise(minutesAgo)} ago";
        }

        int half = values.Count / 2;
        double before = values.Take(half).Sum();
        double after = values.Skip(half).Sum();

        if (before <= 0) return "started this window";

        double ratio = after / before;

        if (ratio >= 2) return "stepped up";
        if (ratio <= 0.5) return "easing off";

        return active >= values.Count / 2 ? "constant" : "on and off";
    }

    /// <summary>Fewer events than this in the whole window is too little to characterise.</summary>
    private const int MinEventsToDescribe = 5;

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
