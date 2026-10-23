using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public sealed class StatusHistoryTests
{
    private static ServerStatus Status(uint? id = 1) => new("not-retained", "1", 0, 7355, 48000, 2, 16,
        id is { } session ? new(session, 10, 0, 30, 20, 0, 0, 0, 0, 0, 0) : null);

    [Fact]
    public void RingRetainsOnlyTheLastHourAndIncrementalReadsDoNotRepeatSamples()
    {
        var history = new StatusHistory();
        for (int second = 1; second <= 4000; second++) history.Add(Status(), second * 1000L);
        var full = history.Read(60, 0, null, 4_000_000);
        Assert.True(full.Reset);
        Assert.Equal(3600, full.Samples.Length);
        Assert.Equal(401, full.Samples[0].Sequence);
        Assert.Equal(4000, full.NextSequence);
        Assert.Empty(history.Read(60, full.NextSequence, full.Epoch, 4_000_000).Samples);
        history.Add(Status(), 4_001_000);
        var incremental = history.Read(60, full.NextSequence, full.Epoch, 4_001_000);
        Assert.False(incremental.Reset);
        Assert.Equal(4001, Assert.Single(incremental.Samples).Sequence);
        var stale = history.Read(60, 1, full.Epoch, 4_001_000);
        Assert.True(stale.Reset);
        Assert.Equal(3600, stale.Samples.Length);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(5, 300)]
    [InlineData(15, 900)]
    [InlineData(60, 3600)]
    public void WindowUsesTimestampsAndPreservesIdleAndSessionBoundaries(int minutes, int expected)
    {
        var history = new StatusHistory();
        for (int second = 1; second <= 3600; second++)
            history.Add(Status(second == 3599 ? null : second == 3600 ? 2u : 1u), second * 1000L);
        var result = history.Read(minutes, 0, null, 3_600_000);
        Assert.Equal(expected, result.Samples.Length);
        Assert.Null(result.Samples[^2].Session);
        Assert.Equal(2u, result.Samples[^1].Session!.Id);
        Assert.Empty(history.Read(1, 0, null, 4_000_000).Samples);
    }

    [Fact]
    public void RestartAndFutureCursorResetWithoutLosingTheNewWindow()
    {
        var first = new StatusHistory();
        first.Add(Status(), 1000);
        var previous = first.Read(1, 0, null, 1000);
        var restarted = new StatusHistory();
        restarted.Add(Status(2), 2000);
        var current = restarted.Read(1, previous.NextSequence, previous.Epoch, 2000);
        Assert.NotEqual(previous.Epoch, current.Epoch);
        Assert.True(current.Reset);
        Assert.Single(current.Samples);
        Assert.True(restarted.Read(1, 999, current.Epoch, 2000).Reset);
        Assert.Throws<ArgumentOutOfRangeException>(() => restarted.Read(61, 0, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => restarted.Read(1, -1, null, 0));
    }

    [Fact]
    public async Task ConcurrentReadersObserveOrderedCoherentBoundedSnapshots()
    {
        var history = new StatusHistory();
        var writer = Task.Run(() => { for (int i = 1; i <= 10_000; i++) history.Add(Status(), i); });
        for (int i = 0; i < 100; i++)
        {
            var result = history.Read(60, 0, null, 10_000);
            Assert.InRange(result.Samples.Length, 0, 3600);
            Assert.True(result.Samples.Zip(result.Samples.Skip(1), (a, b) => b.Sequence == a.Sequence + 1).All(x => x));
            if (result.Samples.Length > 0) Assert.Equal(result.NextSequence, result.Samples[^1].Sequence);
        }
        await writer;
    }
}
