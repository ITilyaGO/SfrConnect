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
        Assert.Single(messages, m => m.Contains("повтор чтения 2/3"));
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
    public async Task ThreeAbsentRepliesReturnNormalFailureWithoutFourthAttempt()
    {
        var replies = Snapshot(2).Concat(Snapshot(2)).Append(WriteAcknowledgement())
            .Concat(Enumerable.Repeat(Frame(0x2A, 6), 3)).Concat(Snapshot(3));
        using var transport = new ScriptedStream(replies.ToArray());
        using var station = new SfrStation(transport);
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
}
