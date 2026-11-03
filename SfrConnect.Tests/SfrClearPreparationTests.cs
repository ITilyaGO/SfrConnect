namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Fact]
    public async Task SingleUnavailableCellWithReadableMarkerRestartsPreparation()
    {
        var clock = new MaintenanceClock();
        bool interrupted = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                memory.Present = true;
                if (!interrupted && request[3] == 1 && request[4] == 0x0D)
                {
                    interrupted = true;
                    memory.Present = false;
                }
            }
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            Assert.Empty(transport.Mutations);
            // A fresh scan must also visit addresses read before the interruption.
            transport.Cells[8] = [31, 1, 2, 3];
            clock.Advance(delay);
        });
        var result = await new SfrNumberWriter(station).AssignAsync(500, clearCard: true);
        Assert.True(result.Success);
        Assert.Equal(500, result.Card!.Number);
        Assert.True(result.SignalSent);
        Assert.Equal(200, clock.Milliseconds);
        Assert.Contains(transport.Mutations, r => r[4] == 8);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationWaitsForReturnOrReturnsUnchangedResult(bool returns)
    {
        var clock = new MaintenanceClock();
        bool removed = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!removed && request[3] == 1 && request[4] == 0x0D)
                {
                    removed = true;
                    memory.Present = false;
                }
            }
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            Assert.Empty(transport.Mutations);
            clock.Advance(delay);
            if (returns && clock.Milliseconds >= 4200) transport.Present = true;
        });
        var result = await new SfrNumberWriter(station).AssignAsync(500, clearCard: true);
        Assert.Equal(returns, result.Success);
        Assert.Equal(returns, result.SignalSent);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(returns ? 4200 : 5000, clock.Milliseconds);
        if (!returns)
        {
            Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
            Assert.False(result.CardMayHaveChanged);
            Assert.Null(result.LastAttemptedCell);
            Assert.Empty(transport.Mutations);
            transport.Present = true;
            Assert.Equal(2, station.ReadCardNumber()!.Number);
            Assert.True((await new SfrNumberWriter(station).AssignAsync(500, clearCard: true)).Success);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationDoesNotModifyReturnedCardWithChangedIdentity(bool changeHeader)
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (request[3] == 1 && request[4] == 0x0D) memory.Present = false;
            }
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            transport.Present = true;
            transport.Cells[changeHeader ? 4 : 1][0] ^= 1;
        });
        var result = await new SfrNumberWriter(station).AssignAsync(500, clearCard: true);
        Assert.False(result.Success);
        Assert.False(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task RepeatedUnreadableCellDoesNotRestartPreparationDeadline()
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => memory.Present = !(request[3] == 1 && request[4] == 0x0D)
        };
        using var station = new SfrStation(transport, options: new SfrStationOptions { VerificationReturnTimeoutMs = 1000 },
            verificationClock: clock, verificationDelay: clock.Advance);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.False(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(1000, clock.Milliseconds);
        Assert.Empty(transport.Mutations);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task CancellationWhileWaitingDuringPreparationDoesNotChangeCard()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => memory.Present = !(request[3] == 1 && request[4] == 0x0D)
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SfrNumberWriter(station).ClearAsync(cancellation.Token));
        Assert.Empty(transport.Mutations);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task UnsupportedCounterReturnsUnchangedResultWithoutUsbFault()
    {
        using var transport = new CardMemory();
        transport.Cells[4][0] = 128;
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
        Assert.False(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Empty(transport.Mutations);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }
}
