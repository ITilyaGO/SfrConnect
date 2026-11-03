namespace SfrConnect.Tests;

public partial class SfrWriteTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TemporaryAbsenceAfterAckRetriesReadingAndSignalsOnce(int lossPoint)
    {
        byte[][] interrupted = lossPoint switch
        {
            0 => [Frame(0x2A, 6)],
            1 => [Marker(), Frame(0x2A, 6)],
            _ => [Marker(), Number(3), Frame(0x2A, 6)]
        };
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(interrupted).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray());
        var messages = new List<string>();
        using var station = new SfrStation(transport, messages.Add);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.True(result.Success);
        Assert.Equal(3, result.Card!.Number);
        Assert.True(result.SignalSent);
        Assert.Null(result.Error);
        Assert.Single(transport.Writes, r => r[3] == 2);
        Assert.Single(transport.Writes, r => r[3] == 3);
        Assert.Single(messages, m => m.Contains("повтор чтения 2 через"));
    }

    [Fact]
    public async Task HeaderUnavailableLikeHardwareLogRecoversWithoutRewriting()
    {
        int headerReads = 0;
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Append(Marker()).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray())
        {
            HeaderReply = () => ++headerReads == 3 ? Frame(0x2A, 6) : Frame(1, 4, 5, 0, 0, 0)
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.True(result.Success);
        Assert.Single(transport.Writes, r => r[3] == 2);
        Assert.True(result.SignalSent);
    }

    [Fact]
    public async Task SecondRetryCanRecoverEvenIfCancellationArrivesAfterWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(new[] { Frame(0x2A, 6), Frame(0x2A, 6) }).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray())
        {
            OnWrite = r => { if (r[3] == 2) cancellation.Cancel(); }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3, cancellation.Token);
        Assert.True(result.Success);
        Assert.Single(transport.Writes, r => r[3] == 2);
    }

    [Fact]
    public async Task ConfiguredDeadlineReturnsNormalFailureWithoutFurtherAttempts()
    {
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(Enumerable.Repeat(Frame(0x2A, 6), 3)).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray());
        using var station = StationWithVerificationTimeout(transport, 400);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.Null(result.Card);
        Assert.Equal(3, result.RequestedNumber);
        Assert.Equal(SfrAssignmentFailure.WriteUnverified, result.Failure);
        Assert.True(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(12, transport.Writes.Count);
        Assert.Single(transport.Writes, r => r[3] == 2);
        Assert.DoesNotContain(transport.Writes, r => r[3] == 3);
        Assert.Equal(3, station.ReadCardNumber()!.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentMarkerOrNumberAfterRetryStopsVerification(bool changedMarker)
    {
        byte[][] returned = changedMarker ? [Marker(4)] : [Marker(), Number(99)];
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Append(Frame(0x2A, 6)).Concat(returned).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray());
        var messages = new List<string>();
        using var station = new SfrStation(transport, messages.Add);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.Single(messages, m => m.Contains("повтор чтения"));
        Assert.Single(transport.Writes, r => r[3] == 2);
        Assert.DoesNotContain(transport.Writes, r => r[3] == 3);
    }

    [Fact]
    public async Task VerificationTimeoutDoesNotRetryUnsynchronizedUsbExchange()
    {
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement());
        using var transport = new ScriptedStream(replies.ToArray());
        var messages = new List<string>();
        using var station = new SfrStation(transport, messages.Add);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.True(result.RequiresReconnect);
        Assert.Equal(10, transport.Writes.Count);
        Assert.DoesNotContain(messages, m => m.Contains("повтор чтения"));
    }

    [Fact]
    public async Task DefaultWindowAllowsCardToReturnAfterFourSeconds()
    {
        var clock = new VerificationTestClock();
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(Enumerable.Repeat(Frame(0x2A, 6), 21)).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray());
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: clock.Delay);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.True(result.Success);
        Assert.True(result.SignalSent);
        Assert.Equal(4200, clock.ElapsedMs);
        Assert.Single(transport.Writes, r => r[3] == 2);
        Assert.Equal(5000, new SfrStationOptions().VerificationReturnTimeoutMs);
    }

    [Theory]
    [InlineData(5000, 200)]
    [InlineData(750, 200)]
    [InlineData(0, 200)]
    [InlineData(500, 1000)]
    public async Task WaitIsBoundedByConfiguredDeadline(int timeout, int interval)
    {
        var clock = new VerificationTestClock();
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(Enumerable.Repeat(Frame(0x2A, 6), 100));
        using var transport = new ScriptedStream(replies.ToArray());
        using var station = new SfrStation(transport, options: new SfrStationOptions
        {
            VerificationReturnTimeoutMs = timeout, VerificationRetryIntervalMs = interval
        }, verificationClock: clock, verificationDelay: clock.Delay);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(timeout, clock.ElapsedMs);
        Assert.Equal((int)Math.Ceiling((double)timeout / interval), clock.Delays);
        Assert.Single(transport.Writes, r => r[3] == 2);
        Assert.DoesNotContain(transport.Writes, r => r[3] == 3);
    }

    [Fact]
    public async Task PartialReappearanceDoesNotRestartDeadline()
    {
        var clock = new VerificationTestClock();
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Append(Frame(0x2A, 6)).Concat(Enumerable.Range(0, 5).SelectMany(_ => new[] { Marker(), Frame(0x2A, 6) }));
        using var transport = new ScriptedStream(replies.ToArray());
        using var station = new SfrStation(transport, options: new SfrStationOptions { VerificationReturnTimeoutMs = 1000 },
            verificationClock: clock, verificationDelay: clock.Delay);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(1000, clock.ElapsedMs);
        Assert.Equal(5, clock.Delays);
    }

    [Fact]
    public async Task SuccessfulFirstVerificationDoesNotWait()
    {
        var clock = new VerificationTestClock();
        using var transport = new ScriptedStream(AssignmentExchange());
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: clock.Delay);
        Assert.True((await new SfrNumberWriter(station).AssignAsync(3)).Success);
        Assert.Equal(0, clock.Delays);
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(5000, 0)]
    [InlineData(5000, -1)]
    public void InvalidVerificationSettingsAreRejectedBeforeUsbAccess(int timeout, int interval)
    {
        using var transport = new ScriptedStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SfrStation(transport, options: new SfrStationOptions
        {
            VerificationReturnTimeoutMs = timeout, VerificationRetryIntervalMs = interval
        }));
        Assert.Empty(transport.Writes);
    }

    private static SfrStation StationWithVerificationTimeout(Stream stream, int timeout)
    {
        var clock = new VerificationTestClock();
        return new SfrStation(stream, options: new SfrStationOptions { VerificationReturnTimeoutMs = timeout },
            verificationClock: clock, verificationDelay: clock.Delay);
    }

    private sealed class VerificationTestClock : TimeProvider
    {
        public long ElapsedMs { get; private set; }
        public int Delays { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ElapsedMs;
        public void Delay(TimeSpan delay)
        {
            ElapsedMs += (long)delay.TotalMilliseconds;
            Delays++;
        }
    }
}
