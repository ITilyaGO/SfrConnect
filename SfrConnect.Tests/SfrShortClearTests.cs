namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(126)]
    [InlineData(127)]
    public async Task ShortClearUsesActualMemoryBoundaryEvenBeyondCounter(byte lastCell)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [5, 0, 0xF0, 0];
        foreach (var cell in transport.Cells.Skip(5)) Array.Clear(cell);
        transport.Cells[lastCell] = [240, 1, 2, 3];
        var identity = transport.Cells[0].ToArray();
        var logs = new List<string>();
        using var station = new SfrStation(transport, logs.Add,
            new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });

        var result = await new SfrNumberWriter(station).AssignAsync(123, clearCard: true);

        Assert.True(result.Success);
        Assert.True(result.SignalSent);
        Assert.True(result.CardCleared);
        Assert.Equal(lastCell, result.ClearedThroughCell);
        Assert.Equal(123, result.Card!.Number);
        Assert.Equal(identity, transport.Cells[0]);
        Assert.Equal(new byte[4], transport.Cells[2]);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, transport.Cells[4]);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
        var firstWrite = transport.Requests.FindIndex(r => r[3] == 2);
        Assert.Contains(transport.Requests.Take(firstWrite), r => r[3] == 1 && r[4] == 127);
        var finalReads = transport.Requests.Skip(transport.Requests.FindLastIndex(r => r[3] == 2) + 1).ToArray();
        for (int cell = 2; cell <= lastCell; cell++) Assert.Contains(finalReads, r => r[3] == 1 && r[4] == cell);
        Assert.DoesNotContain(finalReads, r => r[3] == 1 && r[4] > lastCell);
        Assert.Contains(logs, line => line.Contains($"проверки 02–{lastCell:X2}"));
    }

    [Fact]
    public async Task EmptyCardStillVerifiesFirstPunchCell()
    {
        using var transport = new CardMemory();
        foreach (var cell in transport.Cells.Skip(5)) Array.Clear(cell);
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(result.Success);
        Assert.Equal((byte)5, result.ClearedThroughCell);
        Assert.Equal(2, result.Card!.Number);
    }

    [Fact]
    public async Task FullClearRemainsAvailableAndVerifiesEmptyTail()
    {
        Assert.Equal(SfrClearMode.AfterEachWrite, new SfrStationOptions().ClearMode);
        using var transport = new CardMemory();
        using var station = new SfrStation(transport, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(result.Success);
        Assert.Equal((byte)127, result.ClearedThroughCell);
        var finalReads = transport.Requests.Skip(transport.Requests.FindLastIndex(r => r[3] == 2) + 1);
        Assert.Contains(finalReads, r => r[3] == 1 && r[4] == 127);
    }

    [Theory]
    [InlineData("05050505")]
    [InlineData("00000000")]
    [InlineData("06010000")]
    public async Task ServiceAndUnknownCardsAlwaysVerifyFullRange(string header)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = Convert.FromHexString(header);
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.True(result.Success);
        Assert.True(result.CardCleared);
        Assert.Equal((byte)127, result.ClearedThroughCell);
        var finalReads = transport.Requests.Skip(transport.Requests.FindLastIndex(r => r[3] == 2) + 1);
        Assert.Contains(finalReads, r => r[3] == 1 && r[4] == 127);
    }

    [Fact]
    public async Task ShortModeDoesNotTurnNumberOnlyAssignmentIntoClear()
    {
        using var transport = new CardMemory();
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.True(result.Success);
        Assert.False(result.CardCleared);
        Assert.Null(result.ClearedThroughCell);
        Assert.Equal(3, Assert.Single(transport.Mutations)[4]);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(255)]
    public async Task OutOfProfileCounterCannotExtendMemoryRange(byte counter)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [counter, 0, 0xF0, 0];
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
        Assert.False(result.CardMayHaveChanged);
        Assert.Null(result.ClearedThroughCell);
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task InterruptedPreparationRecalculatesLastOccupiedCell()
    {
        var clock = new MaintenanceClock();
        bool interrupted = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!interrupted && request[3] == 1 && request[4] == 100)
                { interrupted = true; memory.Present = false; }
            }
        };
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell },
            verificationClock: clock, verificationDelay: delay =>
            {
                Assert.Empty(transport.Mutations);
                clock.Advance(delay);
                transport.Present = true;
                transport.Cells[90] = [31, 1, 2, 3];
            });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(result.Success);
        Assert.Equal((byte)90, result.ClearedThroughCell);
        Assert.Equal(new byte[4], transport.Cells[90]);
    }

    [Fact]
    public async Task ReturnDuringVerificationKeepsOriginalBoundaryAfterHeaderReset()
    {
        var clock = new MaintenanceClock();
        bool interrupted = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!interrupted && memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 7)
                { interrupted = true; memory.Present = false; }
            }
        };
        transport.Cells[7] = [240, 1, 2, 3];
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell },
            verificationClock: clock, verificationDelay: delay =>
            {
                clock.Advance(delay);
                transport.Present = true;
                transport.Cells[7] = [240, 9, 9, 9];
            });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(interrupted);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.False(result.RequiresReconnect);
        Assert.Null(result.ClearedThroughCell);
        Assert.Contains("07", result.Error);
        Assert.Equal(200, clock.Milliseconds);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
    }

    [Fact]
    public async Task ShortModeStillRefusesUnreadableTailBeforeWriting()
    {
        using var transport = new CardMemory(32);
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell, VerificationReturnTimeoutMs = 0 });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task ShortClearVerifies2A08WithoutRepeatingWrites()
    {
        using var transport = new CardMemory { WriteReplyCode = 8 };
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(result.Success);
        Assert.Equal((byte)6, result.ClearedThroughCell);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
    }

    [Fact]
    public async Task VerifiedRangeSurvivesSignalFailure()
    {
        using var transport = new CardMemory
        {
            OnRequest = (_, request) => { if (request[3] == 3) throw new IOException("Signal unavailable"); }
        };
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(result.Success);
        Assert.True(result.CardCleared);
        Assert.False(result.SignalSent);
        Assert.Equal((byte)6, result.ClearedThroughCell);
    }

    [Fact]
    public void ShortClearSavesOnlyTheFinalEmptyTailReads()
    {
        using var full = new CardMemory();
        using var fast = new CardMemory();
        using var fullStation = new SfrStation(full, options: new SfrStationOptions { ClearMode = SfrClearMode.FullMemory });
        using var fastStation = new SfrStation(fast,
            options: new SfrStationOptions { ClearMode = SfrClearMode.ThroughLastNonEmptyCell });
        fullStation.ClearCard();
        fastStation.ClearCard();
        Assert.Equal(127 - 6, full.Requests.Count - fast.Requests.Count);
        Assert.Equal(full.Mutations.Count, fast.Mutations.Count);
    }

    [Fact]
    public void UnknownClearModeIsRejectedBeforeExchange()
    {
        using var transport = new CardMemory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SfrStation(transport,
            options: new SfrStationOptions { ClearMode = (SfrClearMode)100 }));
        Assert.Empty(transport.Requests);
    }
}
