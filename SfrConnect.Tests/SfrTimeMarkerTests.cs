using System.Globalization;

namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureDuringMarkerUpdateReportsCorrectCell(bool lostAcknowledgement)
    {
        using var transport = new CardMemory
        {
            LoseWriteAckCell = lostAcknowledgement ? (byte)1 : null,
            OnRequest = (_, request) =>
            {
                if (!lostAcknowledgement && request[3] == 2 && request[4] == 1)
                    throw new IOException("USB disconnected while writing marker.");
            }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.False(result.Success);
        Assert.True(result.CardMayHaveChanged);
        Assert.True(result.RequiresReconnect);
        Assert.Equal((byte)1, result.LastAttemptedCell);
        Assert.Single(transport.Requests, r => r[3] == 2 && r[4] == 1);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EveryAssignmentWritesCurrentTimeEvenWhenNumberMatches(bool sameNumber, bool clear)
    {
        DateTime markerSent = default;
        using var transport = new CardMemory
        {
            OnRequest = (_, request) => { if (request[3] == 2 && request[4] == 1) markerSent = DateTime.Now; }
        };
        transport.Cells[1] = [0, 0x25, 0, 0];
        var before = transport.Cells.Select(c => c.ToArray()).ToArray();
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(sameNumber ? 2 : 123, clearCard: clear);
        Assert.True(result.Success);
        Assert.Equal(clear, result.CardCleared);
        var marker = Assert.Single(transport.Mutations, r => r[4] == 1)[5..9];
        Assert.Contains(Convert.ToHexString(marker), Enumerable.Range(0, 5)
            .Select(seconds => "00" + markerSent.AddSeconds(-seconds).ToString("HHmmss", CultureInfo.InvariantCulture)));
        Assert.Equal(Convert.ToHexString(marker), result.Card!.MarkerHex);
        Assert.Equal(marker, transport.Cells[1]);
        if (sameNumber) Assert.DoesNotContain(transport.Mutations, r => r[4] == 3);
        if (!clear)
            for (int cell = 0; cell < before.Length; cell++)
                if (cell is not 1 and not 3) Assert.Equal(before[cell], transport.Cells[cell]);
    }

    [Theory]
    [InlineData(0x55)]
    [InlineData(8)]
    public async Task UnchangedTimeMarkerNeverReportsAssignmentSuccess(byte reply)
    {
        using var transport = new CardMemory { IgnoreWriteCell = 1, WriteReplyCode = reply };
        transport.Cells[1] = [0, 0x25, 0, 0];
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.False(result.Success);
        Assert.True(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(SfrAssignmentFailure.WriteUnverified, result.Failure);
        Assert.Equal((byte)1, result.LastAttemptedCell);
        Assert.Equal(123, station.ReadCardNumber()!.Number);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Theory]
    [InlineData(true, 0x55)]
    [InlineData(false, 0x55)]
    [InlineData(true, 8)]
    public async Task TimeMarkerVerificationWaitsForReturnWithoutRepeatingWrites(bool returns, byte reply)
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            WriteReplyCode = reply,
            OnRequest = (memory, request) =>
            { if (request[3] == 2 && request[4] == 1) memory.Present = false; }
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            if (returns && clock.Milliseconds >= 4200) transport.Present = true;
        });
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.Equal(returns, result.Success);
        Assert.Equal(returns, result.SignalSent);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(returns ? 4200 : 5000, clock.Milliseconds);
        Assert.Equal(new byte[] { 3, 1 }, transport.Mutations.Select(r => r[4]));
        if (!returns) Assert.Equal((byte)1, result.LastAttemptedCell);
    }

    [Fact]
    public async Task NumberChangedDuringTimeMarkerWriteCannotSucceed()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            { if (request[3] == 2 && request[4] == 1) memory.Cells[3] = [5, 0, 0, 0]; }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }
}
