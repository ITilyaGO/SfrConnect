namespace SfrConnect.Tests;

public class SfrStationTests
{
    [Fact]
    public void BeepSendsOneCompleteReportWithNoExtraReportId()
    {
        using var transport = new MemoryStream();
        using var station = new SfrStation(transport);
        Assert.Empty(transport.ToArray());

        station.Beep();

        var bytes = transport.ToArray();
        Assert.Equal(64, bytes.Length);
        Assert.Equal(new byte[] { 0x3F, 0x05, 0xFD, 0x03, 0x01, 0x04, 0xFE }, bytes[..7]);
        Assert.All(bytes[7..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(0x3F, 63)]
    [InlineData(0x3F, 65)]
    public void UnsupportedDescriptorIsRejected(byte reportId, int length)
    {
        Assert.Throws<NotSupportedException>(() =>
            SfrProtocol.ValidateOutputReports([(reportId, length)]));
    }

    [Fact]
    public void CompatibleReportCanCoexistWithOtherReports()
    {
        SfrProtocol.ValidateOutputReports([(0, 32), (0x3F, 64)]);
    }

    [Fact]
    public void NoOutputReportsIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => SfrProtocol.ValidateOutputReports([]));
    }

    [Fact]
    public void FailedWriteIsNotReportedAsSuccess()
    {
        var messages = new List<string>();
        using var station = new SfrStation(new FailingStream(), messages.Add);

        Assert.Throws<IOException>(() => station.Beep());

        Assert.Single(messages);
        Assert.Contains("TX попытка", messages[0]);
    }

    [Fact]
    public void DisposeClosesConnectionAndPreventsFurtherCommands()
    {
        var transport = new MemoryStream();
        var station = new SfrStation(transport);
        station.Dispose();
        station.Dispose();

        Assert.False(transport.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => station.Beep());
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("USB disconnected");
    }
}
