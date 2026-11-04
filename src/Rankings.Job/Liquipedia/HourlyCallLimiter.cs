namespace Rankings.Job.Liquipedia;

/// <summary>
/// Holds calls back so no more than <c>maxCalls</c> start in any one-hour window.
/// Each run starts on a fresh machine, so pass in the calls saved by earlier runs and
/// save them again through <c>onChange</c>, which fires before each counted call goes out.
/// </summary>
public sealed class HourlyCallLimiter(
    int maxCalls,
    TimeProvider time,
    IEnumerable<DateTimeOffset>? earlierCalls = null,
    Action<IReadOnlyList<DateTimeOffset>>? onChange = null)
{
    static readonly TimeSpan Window = TimeSpan.FromHours(1);

    readonly Queue<DateTimeOffset> _calls = new((earlierCalls ?? []).Order());

    /// <summary>Calls made in the last hour, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> RecentCalls
    {
        get
        {
            Prune(time.GetUtcNow());
            return [.. _calls];
        }
    }

    /// <summary>Calls that can start now without waiting.</summary>
    public int Remaining => Math.Max(0, maxCalls - RecentCalls.Count);

    /// <summary>Waits until a call fits in the budget, then counts it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var now = time.GetUtcNow();
            Prune(now);
            if (_calls.Count < maxCalls)
            {
                _calls.Enqueue(now);
                onChange?.Invoke([.. _calls]);
                return;
            }
            await Task.Delay(_calls.Peek() + Window - now, time, cancellationToken);
        }
    }

    /// <summary>
    /// Counts the budget as spent from now, for when the provider says so (a 429) although
    /// the count disagrees: the next call, in this run or the next, waits a full hour.
    /// </summary>
    public void MarkExhausted()
    {
        var now = time.GetUtcNow();
        _calls.Clear();
        for (var i = 0; i < maxCalls; i++)
            _calls.Enqueue(now);
        onChange?.Invoke([.. _calls]);
    }

    void Prune(DateTimeOffset now)
    {
        while (_calls.Count > 0 && now - _calls.Peek() >= Window)
            _calls.Dequeue();
    }
}
