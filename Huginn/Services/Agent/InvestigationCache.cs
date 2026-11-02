using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Huginn.Services.Agent;

/// <summary>
/// Runs each finding's drill-down once, however many agents ask, and keeps the answer a while.
/// </summary>
/// <remarks>
/// An agent's client gives up on a tool call after a time of its own choosing, which can be
/// shorter than the queries take. The run carries on regardless, so asking again collects it
/// rather than starting over, and agents asking about the same finding at once share one run.
/// </remarks>
public sealed class InvestigationCache(TimeSpan keepFor, Func<DateTimeOffset> now)
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    public InvestigationCache(TimeSpan keepFor) : this(keepFor, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// How long a run that failed is handed out after it failed. Long enough for an asker that
    /// stopped waiting to ask again and collect it, short enough that the next question after
    /// that runs the queries afresh.
    /// </summary>
    public static readonly TimeSpan FailuresKeptFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The answer for <paramref name="key"/>: the one running or kept, or a new run of
    /// <paramref name="run"/>.
    /// </summary>
    public Task<string> AnswerAsync(string key, Func<Task<string>> run)
    {
        lock (_gate)
        {
            DateTimeOffset at = now();

            if (_entries.TryGetValue(key, out Entry? entry) && entry.IsKept(at, keepFor))
                return entry.Answer;

            foreach ((string old, Entry kept) in _entries)
            {
                if (!kept.IsKept(at, keepFor)) _entries.Remove(old);
            }

            Entry started = new(run(), at);
            started.Answer.ContinueWith(
                _ => { lock (_gate) started.Finished = now(); },
                TaskContinuationOptions.ExecuteSynchronously);

            _entries[key] = started;
            return started.Answer;
        }
    }

    private sealed class Entry(Task<string> answer, DateTimeOffset started)
    {
        public Task<string> Answer { get; } = answer;

        public DateTimeOffset Finished { get; set; } = DateTimeOffset.MaxValue;

        /// <summary>Running, answered within the window, or failed too recently to run again.</summary>
        public bool IsKept(DateTimeOffset at, TimeSpan keepFor) =>
            !Answer.IsCompleted
            || (Answer.IsCompletedSuccessfully ? at - started < keepFor : at - Finished < FailuresKeptFor);
    }
}
