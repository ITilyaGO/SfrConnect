namespace SfrConnect.Tests;

public partial class SfrWriteTests
{
    private static byte[][] AssignmentExchange(int oldNumber = 2, int newNumber = 3) =>
        Snapshot(oldNumber).Concat(Snapshot(oldNumber)).Append(WriteAcknowledgement())
            .Concat(Snapshot(newNumber)).ToArray();

    [Fact]
    public async Task AssignmentWaitsThenWritesVerifiesAndSignals()
    {
        using var transport = new ScriptedStream(new[] { Frame(0x2A, 6) }.Concat(AssignmentExchange()).ToArray());
        using var station = new SfrStation(transport);
        var stages = new List<SfrAssignmentStage>();
        var result = await new SfrNumberWriter(station).AssignAsync(3, progress: new InlineProgress(stages.Add));
        Assert.Equal(3, result.Card.Number);
        Assert.True(result.SignalSent);
        Assert.Null(result.SignalError);
        Assert.Equal(new[] { SfrAssignmentStage.WaitingForCard, SfrAssignmentStage.Writing }, stages);
        Assert.Single(transport.Writes, report => report[3] == 2);
        Assert.Equal(3, transport.Writes[^1][3]);
    }

    [Fact]
    public async Task WaitingCanBeCanceledWithoutWritingOrSignaling()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new ScriptedStream(Frame(0x2A, 6)) { OnWrite = _ => cancellation.Cancel() };
        using var station = new SfrStation(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SfrNumberWriter(station).AssignAsync(3, cancellation.Token));
        Assert.DoesNotContain(transport.Writes, report => report[3] != 1);
    }

    [Fact]
    public async Task CancellationDuringPrewriteReadPreventsWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        using var transport = new ScriptedStream(Snapshot(2).Concat(Snapshot(2)).ToArray())
        { OnWrite = report => { if (report[3] == 1 && ++reads == 4) cancellation.Cancel(); } };
        using var station = new SfrStation(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SfrNumberWriter(station).AssignAsync(3, cancellation.Token));
        Assert.DoesNotContain(transport.Writes, report => report[3] != 1);
    }

    [Fact]
    public async Task CancellationAfterWriteStillReturnsVerifiedResult()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new ScriptedStream(AssignmentExchange())
        { OnWrite = report => { if (report[3] == 2) cancellation.Cancel(); } };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3, cancellation.Token);
        Assert.Equal(3, result.Card.Number);
        Assert.True(result.SignalSent);
    }

    [Fact]
    public async Task UnverifiedWriteDoesNotSignalOrRetry()
    {
        using var transport = new ScriptedStream(AssignmentExchange(newNumber: 2));
        using var station = new SfrStation(transport);
        await Assert.ThrowsAsync<SfrWriteUnverifiedException>(() => new SfrNumberWriter(station).AssignAsync(3));
        Assert.Single(transport.Writes, report => report[3] == 2);
        Assert.DoesNotContain(transport.Writes, report => report[3] == 3);
    }

    [Fact]
    public async Task SignalFailureDoesNotLoseVerifiedNumber()
    {
        using var transport = new ScriptedStream(AssignmentExchange()) { FailSignal = true };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.Equal(3, result.Card.Number);
        Assert.False(result.SignalSent);
        Assert.Equal("Signal unavailable.", result.SignalError);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(40000)]
    public async Task AssignmentRejectsInvalidNumberWithoutUsb(int number)
    {
        using var transport = new ScriptedStream();
        using var station = new SfrStation(transport);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SfrNumberWriter(station).AssignAsync(number));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task PreviousCardMustBeRemovedBeforeNextAssignment()
    {
        var frames = Snapshot(3).Append(Frame(0x2A, 6)).Concat(Snapshot(3))
            .Concat(new[] { Frame(0x2A, 6), Frame(0x2A, 6) }).Concat(AssignmentExchange());
        using var transport = new ScriptedStream(frames.ToArray());
        using var station = new SfrStation(transport);
        var stages = new List<SfrAssignmentStage>();
        var result = await new SfrNumberWriter(station).AssignAsync(3,
            progress: new InlineProgress(stages.Add), waitForRemoval: true);
        Assert.Equal(3, result.Card.Number);
        Assert.Equal(new[] { SfrAssignmentStage.WaitingForRemoval, SfrAssignmentStage.WaitingForCard, SfrAssignmentStage.Writing }, stages);
        Assert.Single(transport.Writes, report => report[3] == 2);
    }

    [Fact]
    public async Task ParallelAssignmentIsRejectedAndCanceledWriterCanBeReused()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new ScriptedStream(new[] { Frame(0x2A, 6) }.Concat(AssignmentExchange()).ToArray());
        using var station = new SfrStation(transport);
        var writer = new SfrNumberWriter(station);
        var first = writer.AssignAsync(3, cancellation.Token, new InlineProgress(_ => started.TrySetResult()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AssignAsync(4));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var result = await writer.AssignAsync(3);
        Assert.Equal(3, result.Card.Number);
    }

    private sealed class InlineProgress(Action<SfrAssignmentStage> report) : IProgress<SfrAssignmentStage>
    {
        public void Report(SfrAssignmentStage value) => report(value);
    }
}
