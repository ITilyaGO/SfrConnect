namespace SfrConnect.Tests;

public class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData("00010000", SfrCardKind.Service)]
    [InlineData("01020000", SfrCardKind.Service)]
    [InlineData("02020000", SfrCardKind.Service)]
    [InlineData("03020000", SfrCardKind.Service)]
    [InlineData("05020000", SfrCardKind.Service)]
    [InlineData("09020000", SfrCardKind.Service)]
    [InlineData("01091234", SfrCardKind.Service)]
    [InlineData("02091234", SfrCardKind.Service)]
    [InlineData("03030303", SfrCardKind.Service)]
    [InlineData("05050505", SfrCardKind.Service)]
    [InlineData("05000000", SfrCardKind.Participant)]
    [InlineData("7F000000", SfrCardKind.Participant)]
    [InlineData("00000000", SfrCardKind.Unknown)]
    [InlineData("04000000", SfrCardKind.Unknown)]
    [InlineData("06010000", SfrCardKind.Unknown)]
    public void ClassifiesKnownHeadersAndProtectsUnknownLayouts(string hex, SfrCardKind expected)
        => Assert.Equal(expected, SfrProtocol.ClassifyCard(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("00010000")]
    [InlineData("05050505")]
    [InlineData("00000000")]
    public async Task ProtectedCardsCannotBeChangedThroughAnyEntryPoint(string header)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = Convert.FromHexString(header);
        transport.Cells[3] = [255, 255, 255, 255];
        using var station = new SfrStation(transport);
        var info = station.ReadCardInfo();
        Assert.NotEqual(SfrCardKind.Participant, info!.Kind);
        Assert.Null(info.Number);
        Assert.Throws<SfrProtectedCardException>(() => station.WriteCardNumber(3));
        Assert.Throws<SfrProtectedCardException>(() => station.WriteCardNumber(2, 3));
        Assert.Throws<SfrProtectedCardException>(() => station.WriteCardNumber(3, clearCard: true));
        Assert.Throws<SfrProtectedCardException>(() => station.ClearCard());
        var writer = new SfrNumberWriter(station);
        await Assert.ThrowsAsync<SfrProtectedCardException>(() => writer.AssignAsync(3));
        await Assert.ThrowsAsync<SfrProtectedCardException>(() => writer.AssignAsync(3, clearCard: true));
        await Assert.ThrowsAsync<SfrProtectedCardException>(() => writer.ClearAsync());
        Assert.Empty(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, report => report[3] == 3 || (report[3] == 1 && report[4] == 3));
    }

    [Fact]
    public void ClearPreservesNumberAndCellZeroAndRemovesStaleTail()
    {
        using var transport = new CardMemory();
        transport.Cells[3] = [185, 2, 1, 0xA5];
        transport.Cells[126] = [31, 10, 20, 30];
        byte[] number = transport.Cells[3].ToArray();
        byte[] identity = transport.Cells[0].ToArray();
        using var station = new SfrStation(transport);
        var result = station.ClearCard();
        Assert.Equal(40585, result.Number);
        Assert.Equal(number, transport.Cells[3]);
        Assert.Equal(identity, transport.Cells[0]);
        Assert.Equal(new byte[4], transport.Cells[2]);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, transport.Cells[4]);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
        Assert.Equal(result.MarkerHex, Convert.ToHexString(transport.Cells[1]));
        Assert.DoesNotContain(transport.Mutations, report => report[4] is 0 or 3);
    }

    [Fact]
    public async Task AssignmentWithClearReturnsVerifiedNumberAndClearedFlag()
    {
        using var transport = new CardMemory();
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(123, clearCard: true);
        Assert.Equal(123, result.Card.Number);
        Assert.True(result.CardCleared);
        Assert.True(result.SignalSent);
        Assert.Equal(new byte[] { 123, 0, 0, 0 }, transport.Cells[3]);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
        Assert.Equal(3, transport.Requests[^1][3]);
    }

    [Fact]
    public async Task StandaloneAsyncClearPreservesNumber()
    {
        using var transport = new CardMemory();
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.Equal(2, result.Card.Number);
        Assert.True(result.CardCleared);
        Assert.DoesNotContain(transport.Mutations, report => report[4] == 3);
    }

    [Fact]
    public async Task AssignmentWithoutClearOnlyChangesNumber()
    {
        using var transport = new CardMemory();
        var before = transport.Cells.Select(cell => cell.ToArray()).ToArray();
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3);
        Assert.False(result.CardCleared);
        Assert.Equal(3, Assert.Single(transport.Mutations)[4]);
        for (int i = 0; i < before.Length; i++)
            if (i != 3) Assert.Equal(before[i], transport.Cells[i]);
    }

    [Fact]
    public async Task CancellationDuringPreflightDoesNotClearAnything()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => { if (request[3] == 1 && request[4] == 100) cancellation.Cancel(); }
        };
        using var station = new SfrStation(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SfrNumberWriter(station).AssignAsync(3, cancellation.Token, clearCard: true));
        Assert.Empty(transport.Mutations);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task CancellationAfterFirstMutationStillCompletesVerification()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => { if (request[3] == 2) cancellation.Cancel(); }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(3, cancellation.Token, clearCard: true);
        Assert.True(result.CardCleared);
        Assert.Equal(3, result.Card.Number);
    }

    [Fact]
    public async Task LostAckAfterFirstMutationReportsPartialOperationWithoutRetry()
    {
        using var transport = new CardMemory { LoseWriteAck = true };
        using var station = new SfrStation(transport);
        var error = await Assert.ThrowsAsync<SfrCardUpdateUnverifiedException>(() =>
            new SfrNumberWriter(station).AssignAsync(3, clearCard: true));
        Assert.Equal(3, error.RequestedNumber);
        Assert.Equal(2, error.LastAttemptedCell);
        Assert.Single(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, request => request[3] == 3);
        Assert.Throws<InvalidOperationException>(() => station.ReadCardNumber());
    }

    [Fact]
    public async Task ServiceCardAppearingMidClearStopsFurtherWrites()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => { if (request[3] == 2) memory.Cells[4] = [5, 5, 5, 5]; }
        };
        using var station = new SfrStation(transport);
        var error = await Assert.ThrowsAsync<SfrCardUpdateUnverifiedException>(() => new SfrNumberWriter(station).ClearAsync());
        Assert.IsType<SfrProtectedCardException>(error.InnerException);
        Assert.Single(transport.Mutations);
    }

    [Fact]
    public void FinalVerificationDetectsUnexpectedTailData()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => { if (request[3] == 2 && request[4] == 1) memory.Cells[127] = [1, 2, 3, 4]; }
        };
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardUpdateUnverifiedException>(() => station.ClearCard());
    }

    [Fact]
    public void ShortMemoryIsRejectedBeforeAnyMutation()
    {
        using var transport = new CardMemory(32);
        using var station = new SfrStation(transport);
        Assert.Throws<NotSupportedException>(() => station.ClearCard());
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public void WriteFrameUsesTargetCellInChecksum()
    {
        var report = SfrProtocol.CreateWriteReport(127, [0, 0, 0, 0]);
        Assert.Equal(new byte[] { 0x3F, 9, 0xFD, 2, 127, 0, 0, 0, 0, 129, 0xFE }, report[..11]);
    }

    private sealed class CardMemory : MemoryStream
    {
        private readonly Queue<byte[]> replies = new();
        public byte[][] Cells { get; }
        public List<byte[]> Requests { get; } = [];
        public List<byte[]> Mutations { get; } = [];
        public Action<CardMemory, byte[]>? OnRequest { get; init; }
        public bool LoseWriteAck { get; init; }
        public override int ReadTimeout { get; set; }

        public CardMemory(int count = 128)
        {
            Cells = Enumerable.Range(0, count).Select(_ => new byte[4]).ToArray();
            Cells[0] = [9, 8, 7, 6];
            Cells[1] = [0, 0x19, 0x55, 3];
            Cells[2] = [1, 2, 3, 4];
            Cells[3] = [2, 0, 0, 0];
            Cells[4] = [6, 0, 0, 0];
            Cells[5] = [241, 0x10, 0x20, 0x30];
            Cells[6] = [240, 0x11, 0x20, 0x30];
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var request = buffer.AsSpan(offset, count).ToArray();
            Requests.Add(request);
            OnRequest?.Invoke(this, request);
            byte address = request[4];
            if (request[3] == 1)
            {
                replies.Enqueue(address >= Cells.Length ? Frame(0x2A, 6) : Frame(new byte[] { 1, address }.Concat(Cells[address]).ToArray()));
            }
            else if (request[3] == 2)
            {
                Mutations.Add(request);
                Cells[address] = request[5..9];
                if (!LoseWriteAck) replies.Enqueue(Frame(0x2A, 0x55));
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!replies.TryDequeue(out var reply)) throw new TimeoutException("No reply.");
            reply.CopyTo(buffer, offset);
            return reply.Length;
        }

        private static byte[] Frame(params byte[] payload)
        {
            var frame = new byte[64];
            frame[0] = 0x3F;
            frame[1] = (byte)(payload.Length + 3);
            frame[2] = 0xFD;
            payload.CopyTo(frame, 3);
            frame[payload.Length + 4] = 0xFE;
            return frame;
        }
    }
}
