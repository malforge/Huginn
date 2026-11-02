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
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    public InvestigationCache(TimeSpan keepFor) : this(keepFor, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// The answer for <paramref name="key"/>: the one running or kept, or a new run of
    /// <paramref name="run"/>. A run that failed is not kept, so the next ask tries again.
    /// </summary>
    public Task<string> AnswerAsync(string key, Func<Task<string>> run)
    {
        lock (_gate)
        {
            DateTimeOffset at = now();

            if (_entries.TryGetValue(key, out Entry? entry)
                && (!entry.Answer.IsCompleted || at - entry.Started < keepFor)
                && !entry.Answer.IsFaulted && !entry.Answer.IsCanceled)
            {
                return entry.Answer;
            }

            foreach ((string old, Entry kept) in _entries)
            {
                if (kept.Answer.IsCompleted && at - kept.Started >= keepFor) _entries.Remove(old);
            }

            Task<string> answer = run();
            _entries[key] = new Entry(answer, at);
            return answer;
        }
    }

    private sealed record Entry(Task<string> Answer, DateTimeOffset Started);
}
