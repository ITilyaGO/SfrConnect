namespace SfrConnect.Tests;

public partial class SfrWriteTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CardMovementDuringSearchContinuesWaiting(int failurePoint)
    {
        byte[][] interrupted = failurePoint switch
        {
            0 => [Marker(), Frame(0x2A, 6)],
            1 => [Marker(), Number(2), Frame(0x2A, 6)],
            _ => [Marker(), Number(2), Marker(4)]
        };
        using var transport = new ScriptedStream(interrupted.Concat(AssignmentExchange()).ToArray());
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.Equal(3, result.Card!.Number);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalDuringPrewriteReadRetriesOnlyBeforeMutation(bool afterNumber)
    {
        var interrupted = afterNumber
            ? Snapshot(2).Concat(new[] { Marker(), Number(2), Frame(0x2A, 6) })
            : Snapshot(2).Append(Frame(0x2A, 6));
        using var transport = new ScriptedStream(interrupted.Concat(AssignmentExchange()).ToArray());
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.Equal(3, result.Card!.Number);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
    }

    [Fact]
    public async Task RemovalDuringVerificationDoesNotRepeatWriteOrSignal()
    {
        var frames = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(new[] { Marker(), Number(3), Frame(0x2A, 6), Frame(0x2A, 6), Frame(0x2A, 6) }).Concat(Snapshot(3));
        using var transport = new ScriptedStream(frames.ToArray());
        using var station = StationWithVerificationTimeout(transport, 400);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.Null(result.Card);
        Assert.Equal(SfrAssignmentFailure.WriteUnverified, result.Failure);
        Assert.True(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Contains("недоступен", result.Error);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
        Assert.DoesNotContain(transport.Writes, report => report[3] == 3);
        Assert.Equal(3, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task PartialReadDoesNotCountAsConfirmedRemoval()
    {
        var frames = new[] { Frame(0x2A, 6), Marker(), Number(3), Frame(0x2A, 6), Frame(0x2A, 6) }
            .Concat(Snapshot(3)).Concat(new[] { Frame(0x2A, 6), Frame(0x2A, 6) })
            .Concat(AssignmentExchange());
        using var transport = new ScriptedStream(frames.ToArray());
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3, waitForRemoval: true);
        Assert.Equal(3, result.Card!.Number);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
    }

    [Fact]
    public async Task TimeoutDuringSearchIsNotRetriedAsCardMovement()
    {
        using var transport = new ScriptedStream(Marker());
        using var station = new SfrStation(transport);
        await Assert.ThrowsAsync<TimeoutException>(() => new SfrNumberWriter(station).AssignAsync(3));
        Assert.Equal(3, transport.Writes.Count);
        Assert.Throws<InvalidOperationException>(() => station.ReadCardNumber());
    }

    [Fact]
    public async Task ReadAfterAssignmentWaitsForStationSignalToFinish()
    {
        long? signalStarted = null;
        using var transport = new ScriptedStream(AssignmentExchange().Concat(Snapshot(3)).ToArray())
        {
            OnWrite = report =>
            {
                if (report[3] == 3) signalStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                else if (signalStarted is long started && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 300)
                    throw new TimeoutException("Station is still signaling.");
            }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.True(result.SignalSent);
        Assert.NotNull(signalStarted);
        Assert.Equal(3, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public void ReadAfterManualSignalAlsoWaits()
    {
        long? signalStarted = null;
        using var transport = new ScriptedStream(Snapshot(2))
        {
            OnWrite = report =>
            {
                if (report[3] == 3) signalStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                else if (signalStarted is long started && System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 300)
                    throw new TimeoutException("Station is still signaling.");
            }
        };
        using var station = new SfrStation(transport);
        station.Beep();
        Assert.NotNull(signalStarted);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

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
        Assert.Equal(3, result.Card!.Number);
        Assert.True(result.SignalSent);
        Assert.Null(result.SignalError);
        Assert.Equal(new[] { SfrAssignmentStage.WaitingForCard, SfrAssignmentStage.Writing }, stages);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
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
        { OnWrite = report => { if (report[3] == 1 && ++reads == 5) cancellation.Cancel(); } };
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
        Assert.Equal(3, result.Card!.Number);
        Assert.True(result.SignalSent);
    }

    [Fact]
    public async Task UnverifiedWriteDoesNotSignalOrRetry()
    {
        using var transport = new ScriptedStream(AssignmentExchange(newNumber: 2));
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.WriteUnverified, result.Failure);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
        Assert.DoesNotContain(transport.Writes, report => report[3] == 3);
    }

    [Fact]
    public async Task SignalFailureDoesNotLoseVerifiedNumber()
    {
        using var transport = new ScriptedStream(AssignmentExchange()) { FailSignal = true };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.Equal(3, result.Card!.Number);
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
        Assert.Equal(3, result.Card!.Number);
        Assert.Equal(new[] { SfrAssignmentStage.WaitingForRemoval, SfrAssignmentStage.WaitingForCard, SfrAssignmentStage.Writing }, stages);
        Assert.Single(transport.Writes, report => report[3] == 2 && report[4] == 3);
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
        Assert.Equal(3, result.Card!.Number);
    }

    private sealed class InlineProgress(Action<SfrAssignmentStage> report) : IProgress<SfrAssignmentStage>
    {
        public void Report(SfrAssignmentStage value) => report(value);
    }
}
