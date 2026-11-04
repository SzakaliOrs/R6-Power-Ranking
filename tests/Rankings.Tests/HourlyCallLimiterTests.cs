using Microsoft.Extensions.Time.Testing;
using Rankings.Job.Liquipedia;

namespace Rankings.Tests;

public class HourlyCallLimiterTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-02T04:17:00Z");

    [Fact]
    public async Task WaitAsync_HoldsTheCallPastTheBudgetForAnHour()
    {
        var time = new FakeTimeProvider(Start);
        var limiter = new HourlyCallLimiter(2, time);
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        var third = limiter.WaitAsync();
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(third.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(1));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        // Both earlier calls have just left the window.
        Assert.Equal([Start + TimeSpan.FromHours(1)], limiter.RecentCalls);
    }

    [Fact]
    public async Task WaitAsync_CountsCallsFromThePreviousRun()
    {
        var time = new FakeTimeProvider(Start);
        var limiter = new HourlyCallLimiter(2, time, [Start - TimeSpan.FromMinutes(30), Start - TimeSpan.FromMinutes(10)]);

        var call = limiter.WaitAsync();
        time.Advance(TimeSpan.FromMinutes(29));
        Assert.False(call.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(1));
        await call.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MarkExhausted_MakesTheNextCallWaitAFullHour()
    {
        var time = new FakeTimeProvider(Start);
        var saved = new List<IReadOnlyList<DateTimeOffset>>();
        var limiter = new HourlyCallLimiter(60, time, onChange: saved.Add);

        limiter.MarkExhausted();
        var call = limiter.WaitAsync();
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(call.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(1));
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(60, saved[0].Count);
    }

    [Fact]
    public async Task WaitAsync_StopsInsteadOfWaitingLongerThanAllowed()
    {
        var time = new FakeTimeProvider(Start);
        var saved = new List<IReadOnlyList<DateTimeOffset>>();
        var limiter = new HourlyCallLimiter(1, time, [Start - TimeSpan.FromMinutes(20)], saved.Add, TimeSpan.Zero);

        var budgetSpent = await Assert.ThrowsAsync<LpdbBudgetSpentException>(() => limiter.WaitAsync());

        Assert.Equal(Start + TimeSpan.FromMinutes(40), budgetSpent.NextCallAt);
        // The refused call was not counted.
        Assert.Empty(saved);
        Assert.Single(limiter.RecentCalls);
    }

    [Fact]
    public async Task WaitAsync_WithoutAWaitLimitStillCallsRightAwayWithinBudget()
    {
        var limiter = new HourlyCallLimiter(1, new FakeTimeProvider(Start), maxWait: TimeSpan.Zero);

        await limiter.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, limiter.Remaining);
    }

    [Fact]
    public void CallLogFile_RoundTripsAndStartsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpdb-calls-{Guid.NewGuid():N}.json");
        try
        {
            var log = new CallLogFile(path);
            Assert.Empty(log.Load());

            log.Save([Start, Start + TimeSpan.FromMinutes(1)]);

            Assert.Equal([Start, Start + TimeSpan.FromMinutes(1)], log.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CallLogFile_RefusesACorruptLog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpdb-calls-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json");

            Assert.Throws<InvalidDataException>(() => new CallLogFile(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecentCalls_DropsCallsOlderThanAnHour()
    {
        var time = new FakeTimeProvider(Start);
        var limiter = new HourlyCallLimiter(60, time, [Start - TimeSpan.FromMinutes(61), Start - TimeSpan.FromMinutes(5)]);

        Assert.Equal([Start - TimeSpan.FromMinutes(5)], limiter.RecentCalls);
    }
}
