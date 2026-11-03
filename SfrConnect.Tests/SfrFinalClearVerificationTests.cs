namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0x40, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(1, 3)]
    public async Task CardReturningDuringFinalClearVerificationRestartsFullReadOnlyPass(byte cell, int occurrence)
    {
        var clock = new MaintenanceClock();
        int matchingReads = 0, mutationsAtLoss = 0, resumeIndex = 0;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == cell && ++matchingReads == occurrence)
                {
                    mutationsAtLoss = memory.Mutations.Count;
                    memory.Present = false;
                }
            }
        };
        using var station = new SfrStation(transport, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory }, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            if (clock.Milliseconds >= 4200)
            {
                resumeIndex = transport.Requests.Count;
                transport.Present = true;
            }
        });
        var result = await new SfrNumberWriter(station).AssignAsync(56, clearCard: true);
        Assert.True(result.Success);
        Assert.True(result.CardCleared);
        Assert.True(result.SignalSent);
        Assert.Equal(56, result.Card!.Number);
        Assert.Equal(4200, clock.Milliseconds);
        Assert.Equal(mutationsAtLoss, transport.Mutations.Count);
        var afterReturn = transport.Requests.Skip(resumeIndex).ToArray();
        for (int address = 2; address <= 127; address++)
            Assert.Contains(afterReturn, r => r[3] == 1 && r[4] == address);
        Assert.Single(transport.Requests, r => r[3] == 3);
        Assert.Equal(56, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task FinalPassAbsenceReturnsFalseAndKeepsConnectionUsable()
    {
        var clock = new MaintenanceClock();
        bool removed = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!removed && memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 0x40)
                {
                    memory.Present = false;
                    removed = true;
                }
            }
        };
        using var station = new SfrStation(transport, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory }, verificationClock: clock, verificationDelay: clock.Advance);
        var result = await new SfrNumberWriter(station).AssignAsync(56, clearCard: true);
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.True(result.CardMayHaveChanged);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.Equal(5000, clock.Milliseconds);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
        transport.Present = true;
        Assert.Equal(56, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task RepeatedLossInFinalPassDoesNotResetReturnDeadline()
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 0x40)
                    memory.Present = false;
            }
        };
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { VerificationReturnTimeoutMs = 1000, ClearMode = SfrClearMode.FullMemory },
            verificationClock: clock, verificationDelay: delay => { clock.Advance(delay); transport.Present = true; });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(1000, clock.Milliseconds);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedMemoryOrMarkerOnReturnIsRejectedWithoutDisconnect(bool markerChanged)
    {
        var clock = new MaintenanceClock();
        bool removed = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!removed && memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 0x40)
                {
                    memory.Present = false;
                    removed = true;
                }
            }
        };
        using var station = new SfrStation(transport, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory }, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            transport.Cells[markerChanged ? 1 : 5][0] ^= 1;
            transport.Present = true;
        });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task TransportTimeoutInFinalPassStillRequiresReconnection()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 0x40)
                    throw new TimeoutException("USB read interrupted");
            }
        };
        using var station = new SfrStation(transport, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.True(result.RequiresReconnect);
        Assert.Throws<InvalidOperationException>(() => station.ReadCardNumber());
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Fact]
    public async Task ChangedParticipantCounterInLastCheckCannotPassClearVerification()
    {
        int headerReads = 0;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 4 && ++headerReads == 2)
                    memory.Cells[4][0] = 6;
            }
        };
        using var station = new SfrStation(transport, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.Contains("Заголовок", result.Error);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }
}
