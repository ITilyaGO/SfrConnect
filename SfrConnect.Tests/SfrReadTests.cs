namespace SfrConnect.Tests;

public class SfrReadTests
{
    // Captured from the connected station; padding is nonzero and checksum slots are zero.
    private const string MarkerReply =
        "3F09FD01010018563800FE06F5D2CE9B71EDAEEF2A7AFD7DF9BF0BB17BFFDA771B2CA83D7325ACEFBBEA9FF3A3D7F7B7715BEB67D7D2AB2D6D57F061BFE5675B";
    private const string NumberReply =
        "3F09FD0103B902000000FE6761B7DDEAFC3954CEB47729DE6F612757EE6F47F2A8EEC0D509F1FA82FC7E1AFFBB34BE7A1FAFFA3E4A12F96AFD39AD1B05E73E89";
    private const string UnavailableReply =
        "3F05FD2A0600FE000000FE6761B7DDEAFC3954CEB47729DE6F612757EE6F47F2A8EEC0D509F1FA82FC7E1AFFBB34BE7A1FAFFA3E4A12F96AFD39AD1B05E73E89";

    [Fact]
    public void CardUnavailableIsNotAConnectionTimeoutAndCanBeRetried()
    {
        using var transport = new ScriptedStream(Convert.FromHexString(UnavailableReply),
            Cell(1, 1, 2, 3, 4), Cell(3, 2, 0, 0, 0), Cell(1, 1, 2, 3, 4));
        using var station = new SfrStation(transport);
        Assert.Null(station.ReadCardNumber());
        Assert.Single(transport.Writes);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovalDuringReadDoesNotReturnAPartialResult(bool duringFinalCheck)
    {
        var responses = new List<byte[]> { Cell(1, 1, 2, 3, 4) };
        if (duringFinalCheck)
        {
            responses.Add(Cell(3, 185, 2, 0, 0));
        }
        responses.Add(Convert.FromHexString(UnavailableReply));
        using var transport = new ScriptedStream(responses.ToArray());
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardNotReadyException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void OtherErrorCodesAreNotTreatedAsCardUnavailable()
    {
        var response = Convert.FromHexString(UnavailableReply);
        response[4] = 0x07;
        using var transport = new ScriptedStream(response);
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidDataException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void NoReplyIsATimeoutNotAnAbsentCard()
    {
        using var transport = new ScriptedStream();
        using var station = new SfrStation(transport);
        Assert.Throws<TimeoutException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void BeepReplyDoesNotReplaceTheRequestedCell()
    {
        var beepReply = Convert.FromHexString(UnavailableReply);
        beepReply[3] = 3;
        beepReply[4] = 1;
        using var transport = new ScriptedStream(beepReply,
            Cell(1, 1, 2, 3, 4), Cell(3, 185, 2, 0, 0), Cell(1, 1, 2, 3, 4));
        using var station = new SfrStation(transport);
        Assert.Equal(585, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public void ReadsActualCapturedNumberAndIgnoresPadding()
    {
        var messages = new List<string>();
        using var transport = new ScriptedStream(
            Convert.FromHexString(MarkerReply), Convert.FromHexString(NumberReply), Convert.FromHexString(MarkerReply));
        using var station = new SfrStation(transport, messages.Add);

        var card = station.ReadCardNumber();

        Assert.Equal(new SfrCardNumber(585, "00185638"), card);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(Convert.FromHexString("3F05FD010102FE"), transport.Writes[0][..7]);
        Assert.Equal(Convert.FromHexString("3F05FD010304FE"), transport.Writes[1][..7]);
        Assert.Equal(transport.Writes[0], transport.Writes[2]);
        Assert.All(transport.Writes, report =>
        {
            Assert.Equal(64, report.Length);
            Assert.All(report[7..], value => Assert.Equal(0, value));
        });
        Assert.Equal(3, messages.Count(message => message.StartsWith("RX ")));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(199, 199, 199, 7999999)]
    [InlineData(1, 2, 3, 120401)]
    public void DecodesAllThreeBase200Digits(byte low, byte middle, byte high, int expected)
    {
        using var transport = new ScriptedStream(
            Cell(1, 0, 0, 0, 0), Cell(3, low, middle, high, 0), Cell(1, 0, 0, 0, 0));
        using var station = new SfrStation(transport);
        Assert.Equal(expected, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public void ChipMarkerChangeDoesNotReturnAClaimedNumber()
    {
        using var transport = new ScriptedStream(
            Cell(1, 1, 2, 3, 4), Cell(3, 185, 2, 0, 0), Cell(1, 4, 3, 2, 1));
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardNotReadyException>(() => station.ReadCardNumber());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1, 63)]
    [InlineData(2, 0)]
    [InlineData(10, 0)]
    public void RejectsMalformedFrame(int index, byte value)
    {
        var response = Cell(1, 1, 2, 3, 4);
        response[index] = value;
        using var transport = new ScriptedStream(response);
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidDataException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void TruncatedReportIsNotDecoded()
    {
        using var transport = new ScriptedStream(Cell(1, 1, 2, 3, 4)[..10]);
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidDataException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void UnknownResponseIsNotTreatedAsNoCard()
    {
        var response = Cell(1, 1, 2, 3, 4);
        response[3] = 0x7F;
        using var transport = new ScriptedStream(response);
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidDataException>(() => station.ReadCardNumber());
    }

    [Theory]
    [InlineData(200, 0, 0)]
    [InlineData(0, 200, 0)]
    [InlineData(0, 0, 200)]
    public void InvalidNumberDigitsAreRejected(byte low, byte middle, byte high)
    {
        using var transport = new ScriptedStream(
            Cell(1, 1, 2, 3, 4), Cell(3, low, middle, high, 0), Cell(1, 1, 2, 3, 4));
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidDataException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void LateRepliesCannotBeUsedAfterTimeout()
    {
        using var transport = new ScriptedStream(Cell(1, 1, 2, 3, 4));
        using var station = new SfrStation(transport);
        Assert.Throws<TimeoutException>(() => station.ReadCardNumber());
        Assert.Throws<InvalidOperationException>(() => station.ReadCardNumber());
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public void ReadOnDisposedConnectionIsRejected()
    {
        var station = new SfrStation(new ScriptedStream());
        station.Dispose();
        Assert.Throws<ObjectDisposedException>(() => station.ReadCardNumber());
    }

    [Fact]
    public void RepliesForAnotherCellAreSkipped()
    {
        using var transport = new ScriptedStream(
            Cell(3, 1, 0, 0, 0), Cell(1, 1, 2, 3, 4),
            Cell(1, 1, 2, 3, 4), Cell(3, 185, 2, 0, 0), Cell(1, 1, 2, 3, 4));
        using var station = new SfrStation(transport);
        Assert.Equal(585, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public void ClosedTransportIsAnErrorNotNoCard()
    {
        using var transport = new ScriptedStream(Array.Empty<byte>());
        using var station = new SfrStation(transport);
        Assert.Throws<IOException>(() => station.ReadCardNumber());
    }

    private static byte[] Cell(byte position, params byte[] data)
    {
        var report = new byte[64];
        new byte[] { 0x3F, 9, 0xFD, 1, position }.CopyTo(report, 0);
        data.CopyTo(report, 5);
        report[10] = 0xFE;
        return report;
    }

    private sealed class ScriptedStream(params byte[][] responses) : MemoryStream
    {
        private readonly Queue<byte[]> responses = new(responses);
        public List<byte[]> Writes { get; } = [];
        public override int ReadTimeout { get; set; }

        public override void Write(byte[] buffer, int offset, int count) =>
            Writes.Add(buffer.AsSpan(offset, count).ToArray());

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!responses.TryDequeue(out var response))
            {
                throw new TimeoutException("Simulated station timeout.");
            }
            response.CopyTo(buffer, offset);
            return response.Length;
        }
    }
}
