namespace SfrConnect.Tests;

public partial class SfrWriteTests
{
    [Fact]
    public void CanWriteWithoutKnowingCurrentNumber()
    {
        using var transport = Exchange(585, 123, reservedByte: 0xA5);
        using var station = new SfrStation(transport);
        Assert.Equal(123, station.WriteCardNumber(123).Number);
        var write = Assert.Single(transport.Writes, report => report[3] == 2);
        Assert.Equal(new byte[] { 123, 0, 0, 0 }, write[5..9]);
    }

    [Fact]
    public void AutomaticCurrentNumberStillRequiresVerifiedReadback()
    {
        using var transport = Exchange(585, 585);
        using var station = new SfrStation(transport);
        Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(123));
    }

    [Fact]
    public void AutomaticCurrentNumberDoesNotRewriteMatchingNumber()
    {
        using var transport = new ScriptedStream(Snapshot(123));
        using var station = new SfrStation(transport);
        Assert.Equal(123, station.WriteCardNumber(123).Number);
        Assert.DoesNotContain(transport.Writes, report => report[3] == 2);
    }

    [Fact]
    public void MatchingNumberStillClearsNonzeroFourthByte()
    {
        using var transport = Exchange(123, 123, reservedByte: 0xA5);
        using var station = new SfrStation(transport);
        Assert.Equal(123, station.WriteCardNumber(123).Number);
        var write = Assert.Single(transport.Writes, report => report[3] == 2);
        Assert.Equal(new byte[] { 123, 0, 0, 0 }, write[5..9]);
    }

    [Fact]
    public void MatchingNumberWithUnclearedFourthByteFailsReadback()
    {
        using var transport = new ScriptedStream(Snapshot(123, 0xA5)
            .Concat(new[] { WriteAcknowledgement() }).Concat(Snapshot(123, 0xA5)).ToArray());
        using var station = new SfrStation(transport);
        Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(123));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(40000)]
    public void ShortOverloadValidatesNumberBeforeUsbAccess(int number)
    {
        using var transport = new ScriptedStream();
        using var station = new SfrStation(transport);
        Assert.Throws<ArgumentOutOfRangeException>(() => station.WriteCardNumber(number));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void ChangesOnlyNumberCellZerosFourthByteAndVerifiesReadback()
    {
        var log = new List<string>();
        using var transport = Exchange(2, 3, reservedByte: 0xA5);
        using var station = new SfrStation(transport, log.Add);

        var card = station.WriteCardNumber(2, 3);

        Assert.Equal(new SfrCardNumber(3, "00195503"), card);
        var write = Assert.Single(transport.Writes, report => report[3] == 2);
        Assert.Equal(Convert.FromHexString("3F09FD02030300000008FE"), write[..11]);
        Assert.Equal(64, write.Length);
        Assert.All(write[11..], value => Assert.Equal(0, value));
        Assert.Equal(new byte[] { 1, 4, 3, 1, 3, 1, 4, 3, 1 }, transport.Writes.Select(report => report[4]).ToArray());
        Assert.Contains(log, message => message.Contains("Запись подтверждена повторным чтением"));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(199, 199, 0)]
    [InlineData(200, 0, 1)]
    [InlineData(39999, 199, 199)]
    public void EncodesManufacturerNumberRangeWithTwoTrailingZeros(int number, byte low, byte high)
    {
        Assert.Equal(new byte[] { low, high, 0, 0 },
            SfrProtocol.EncodeCardNumber(number));
    }

    [Fact]
    public void WriteChecksumCarriesOverflow()
    {
        var report = SfrProtocol.CreateNumberWriteReport([199, 199, 0, 0xA5]);
        Assert.Equal(0x3A, report[9]);
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(8000000, 3)]
    [InlineData(2, -1)]
    [InlineData(2, 40000)]
    public void InvalidArgumentsSendNothing(int expected, int desired)
    {
        using var transport = new ScriptedStream();
        using var station = new SfrStation(transport);
        Assert.Throws<ArgumentOutOfRangeException>(() => station.WriteCardNumber(expected, desired));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public void WrongCurrentNumberPreventsWrite()
    {
        using var transport = new ScriptedStream(Snapshot(585));
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidOperationException>(() => station.WriteCardNumber(2, 3));
        Assert.DoesNotContain(transport.Writes, report => report[3] == 2);
    }

    [Fact]
    public void NoCardPreventsWrite()
    {
        using var transport = new ScriptedStream(Frame(0x2A, 6));
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardNotReadyException>(() => station.WriteCardNumber(2, 3));
        Assert.DoesNotContain(transport.Writes, report => report[3] == 2);
    }

    [Fact]
    public void CardRemovedBeforeWritePreventsMutation()
    {
        using var transport = new ScriptedStream(Marker(), Number(2), Frame(0x2A, 6));
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardNotReadyException>(() => station.WriteCardNumber(2, 3));
        Assert.DoesNotContain(transport.Writes, report => report[3] == 2);
    }

    [Fact]
    public void AlreadyAssignedNumberDoesNotRewriteMemory()
    {
        using var transport = new ScriptedStream(Snapshot(3));
        using var station = new SfrStation(transport);
        Assert.Equal(3, station.WriteCardNumber(3, 3).Number);
        Assert.DoesNotContain(transport.Writes, report => report[3] == 2);
    }

    [Fact]
    public void AcknowledgementWithoutChangedDataIsNotSuccess()
    {
        var log = new List<string>();
        using var transport = Exchange(2, 2);
        using var station = new SfrStation(transport, log.Add);
        var error = Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
        Assert.Equal(3, error.RequestedNumber);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.DoesNotContain(log, message => message.Contains("Запись подтверждена"));
    }

    [Fact]
    public void VerifiesFourthByteAsWellAsNumber()
    {
        using var transport = new ScriptedStream(
            Snapshot(2, 0xA5).Concat(new[] { WriteAcknowledgement() }).Concat(Snapshot(3, 0xA4)).ToArray());
        using var station = new SfrStation(transport);
        Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
    }

    [Fact]
    public void ChangedMarkerAfterWriteCannotPassVerification()
    {
        using var transport = new ScriptedStream(Snapshot(2)
            .Concat(new[] { WriteAcknowledgement(), Marker(4), Number(3), Marker(4) }).ToArray());
        using var station = new SfrStation(transport);
        Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
    }

    [Fact]
    public void RemovedCardAfterWriteDoesNotReportSuccess()
    {
        using var transport = new ScriptedStream(
            Snapshot(2).Concat(new[] { WriteAcknowledgement(), Frame(0x2A, 6) }).ToArray());
        using var station = new SfrStation(transport);
        Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
    }

    [Fact]
    public void LostAcknowledgementDoesNotRetryOrRollback()
    {
        using var transport = new ScriptedStream(Snapshot(2));
        using var station = new SfrStation(transport);
        var error = Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Throws<InvalidOperationException>(() => station.WriteCardNumber(2, 3));
        Assert.Throws<InvalidOperationException>(() => station.ReadCardNumber());
        Assert.Single(transport.Writes, report => report[3] == 2);
        Assert.Equal(5, transport.Writes.Count);
    }

    [Fact]
    public void FailedUsbWriteIsReportedAsUncertain()
    {
        using var transport = new ScriptedStream(Snapshot(2)) { FailWrite = true };
        using var station = new SfrStation(transport);
        var error = Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
        Assert.IsType<IOException>(error.InnerException);
        Assert.Single(transport.Writes, report => report[3] == 2);
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(2, 4)]
    [InlineData(0x2A, 6)]
    [InlineData(0x7F, 0)]
    public void UnexpectedWriteResponseFailsVerification(byte command, byte code)
    {
        using var transport = new ScriptedStream(Snapshot(2).Append(Frame(command, code)).ToArray());
        using var station = new SfrStation(transport);
        Assert.Throws<SfrWriteUnverifiedException>(() => station.WriteCardNumber(2, 3));
    }

    [Fact]
    public void DisposedStationCannotWrite()
    {
        using var transport = new ScriptedStream();
        var station = new SfrStation(transport);
        station.Dispose();
        Assert.Throws<ObjectDisposedException>(() => station.WriteCardNumber(2, 3));
        Assert.Empty(transport.Writes);
    }

    private static byte[] WriteAcknowledgement() => Convert.FromHexString(
        "3F05FD2A5500FE000000FE06F5D2CE9B71EDAEEF2A7AFD7DF9BF0BB17BFFDA771B2CA83D7325ACEFBBEA9FF3A3D7F7B7715BEB67D7D2AB2D6D57F061BFE5675B");
    private static ScriptedStream Exchange(int oldNumber, int readbackNumber, byte reservedByte = 0) =>
        new(Snapshot(oldNumber, reservedByte).Concat(new[] { WriteAcknowledgement() })
            .Concat(Snapshot(readbackNumber)).ToArray());

    private static byte[][] Snapshot(int number, byte reservedByte = 0) =>
        [Marker(), Number(number, reservedByte), Marker()];

    private static byte[] Marker(byte lastByte = 3) => Frame(1, 1, 0, 0x19, 0x55, lastByte);

    private static byte[] Number(int number, byte reservedByte = 0) =>
        Frame(1, 3, (byte)(number % 200), (byte)(number / 200), 0, reservedByte);

    private static byte[] Frame(params byte[] payload)
    {
        var response = new byte[64];
        response[0] = 0x3F;
        response[1] = (byte)(payload.Length + 3);
        response[2] = 0xFD;
        payload.CopyTo(response, 3);
        response[payload.Length + 4] = 0xFE;
        return response;
    }

    private sealed class ScriptedStream(params byte[][] responses) : MemoryStream
    {
        private bool headerRequested;
        private readonly Queue<byte[]> responses = new(responses);
        public List<byte[]> Writes { get; } = [];
        public bool FailWrite { get; init; }
        public bool FailSignal { get; init; }
        public Action<byte[]>? OnWrite { get; init; }
        public override int ReadTimeout { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var report = buffer.AsSpan(offset, count).ToArray();
            Writes.Add(report);
            headerRequested = report[3] == 1 && report[4] == 4;
            OnWrite?.Invoke(report);
            if (FailSignal && report[3] == 3) throw new IOException("Signal unavailable.");
            if (FailWrite && report[3] == 2)
            {
                throw new IOException("USB disconnected during write.");
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (headerRequested)
            {
                headerRequested = false;
                var header = Frame(1, 4, 5, 0, 0, 0);
                header.CopyTo(buffer, offset);
                return header.Length;
            }
            if (!responses.TryDequeue(out var response))
            {
                throw new TimeoutException("Simulated station timeout.");
            }
            response.CopyTo(buffer, offset);
            return response.Length;
        }
    }
}
