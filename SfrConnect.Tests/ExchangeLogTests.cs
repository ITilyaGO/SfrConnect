using SfrConnect.TestApp;

namespace SfrConnect.Tests;

public class ExchangeLogTests
{
    private static readonly DateTime Timestamp = new(2026, 11, 2, 12, 34, 56, 789);

    [Fact]
    public void BurstWithoutUiConsumerRetainsOnlyLast500Entries()
    {
        var log = new ExchangeLog();
        for (int i = 0; i < 100_000; i++) log.Add($"packet {i}", Timestamp);

        var lines = log.TakeUpdate()!.Split(Environment.NewLine);
        Assert.Equal(500, lines.Length);
        Assert.Equal("[12:34:56.789] packet 99500", lines[0]);
        Assert.Equal("[12:34:56.789] packet 99999", lines[^1]);
        Assert.Null(log.TakeUpdate());
    }

    [Fact]
    public void IdleTicksDoNotRequestRedrawAndExportIncludesPendingMessages()
    {
        var log = new ExchangeLog();
        Assert.Null(log.TakeUpdate());
        log.Add("first", Timestamp);
        Assert.Equal(log.Snapshot(), log.TakeUpdate());
        Assert.Null(log.TakeUpdate());
        log.Add("second", Timestamp.AddMilliseconds(1));
        var export = log.Snapshot();
        Assert.EndsWith("[12:34:56.790] second", export);
        Assert.Equal(export, log.TakeUpdate());
        Assert.Null(log.TakeUpdate());
    }

    [Fact]
    public async Task ConcurrentProducerAndUiSnapshotsPreserveFinalEntries()
    {
        var log = new ExchangeLog();
        using var started = new ManualResetEventSlim();
        var producer = Task.Run(() =>
        {
            started.Set();
            for (int i = 0; i < 10_000; i++) log.Add($"packet {i}", Timestamp);
        });
        started.Wait();
        for (int i = 0; i < 1_000; i++)
        {
            var update = log.TakeUpdate();
            if (update is not null) Assert.InRange(update.Split(Environment.NewLine).Length, 1, 500);
        }
        await producer;
        var lines = log.Snapshot().Split(Environment.NewLine);
        Assert.Equal(500, lines.Length);
        Assert.EndsWith("packet 9500", lines[0]);
        Assert.EndsWith("packet 9999", lines[^1]);
    }
}
