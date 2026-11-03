namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData("0600F000", false)]
    [InlineData("0500F1A5", false)]
    [InlineData("0600F000", true)]
    public async Task ParticipantMetadataCanBeReadAndAssignedWithoutClearing(string header, bool strict)
    {
        using var transport = new CardMemory();
        transport.Cells[1] = [0, 0x08, 0x31, 0x11];
        transport.Cells[4] = Convert.FromHexString(header);
        var before = transport.Cells.Select(cell => cell.ToArray()).ToArray();
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { FormatUnknownCards = !strict });
        var info = station.ReadCardInfo();
        Assert.Equal(SfrCardKind.Participant, info!.Kind);
        Assert.Equal(header, info.HeaderHex);
        Assert.Equal(2, info.Number);

        var result = await new SfrNumberWriter(station).AssignAsync(123);

        Assert.True(result.Success);
        Assert.True(result.SignalSent);
        Assert.False(result.CardCleared);
        Assert.Equal(123, result.Card!.Number);
        Assert.Equal(new byte[] { 3, 1 }, transport.Mutations.Select(r => r[4]));
        for (int cell = 0; cell < before.Length; cell++)
            if (cell is not 1 and not 3) Assert.Equal(before[cell], transport.Cells[cell]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParticipantMetadataIsResetOnlyWhenClearing(bool assign)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [6, 0, 0xF0, 0];
        using var station = new SfrStation(transport);
        var writer = new SfrNumberWriter(station);

        var result = assign ? await writer.AssignAsync(123, clearCard: true) : await writer.ClearAsync();

        Assert.True(result.Success);
        Assert.True(result.SignalSent);
        Assert.True(result.CardCleared);
        Assert.Equal(assign ? 123 : 2, result.Card!.Number);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, transport.Cells[4]);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
    }

    [Theory]
    [InlineData("00000000", 0)]
    [InlineData("06010000", 1)]
    [InlineData("FFFFFFFF", 2)]
    [InlineData("04000000", 3)]
    [InlineData("06010000", 4)]
    [InlineData("00000000", 5)]
    [InlineData("FFFFFFFF", 6)]
    public async Task UnknownHeadersAreNormalizedThroughEveryWriteEntryPoint(string header, int entryPoint)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = Convert.FromHexString(header);
        transport.Cells[126] = [31, 10, 20, 30];
        var identity = transport.Cells[0].ToArray();
        var logs = new List<string>();
        using var station = new SfrStation(transport, logs.Add);
        var writer = new SfrNumberWriter(station);

        SfrCardNumber card;
        if (entryPoint < 4)
        {
            card = entryPoint switch
            {
                0 => station.WriteCardNumber(123),
                1 => station.WriteCardNumber(2, 123),
                2 => station.WriteCardNumber(123, clearCard: true),
                _ => station.ClearCard()
            };
        }
        else
        {
            var result = entryPoint switch
            {
                4 => await writer.AssignAsync(123),
                5 => await writer.AssignAsync(123, clearCard: true),
                _ => await writer.ClearAsync()
            };
            Assert.True(result.Success);
            Assert.True(result.CardCleared);
            Assert.True(result.SignalSent);
            card = result.Card!;
        }

        Assert.Equal(entryPoint is 3 or 6 ? 2 : 123, card.Number);
        Assert.Equal(identity, transport.Cells[0]);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, transport.Cells[4]);
        Assert.Equal(new byte[4], transport.Cells[2]);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
        Assert.Equal(4, transport.Mutations[0][4]);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
        Assert.Contains(logs, line => line.Contains("Unknown") && line.Contains(header));
        Assert.Equal(SfrCardKind.Participant, station.ReadCardInfo()!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownHeaderWithInvalidNumberCanBeFormatted(bool assign)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = new byte[4];
        transport.Cells[3] = [255, 255, 255, 255];
        using var station = new SfrStation(transport);
        var writer = new SfrNumberWriter(station);
        var result = assign ? await writer.AssignAsync(123) : await writer.ClearAsync();
        Assert.True(result.Success);
        Assert.True(result.CardCleared);
        Assert.Equal(assign ? 123 : 0, result.Card!.Number);
    }

    [Fact]
    public async Task UnknownHeaderDoesNotBypassUnreadableMemoryCheck()
    {
        using var transport = new CardMemory(32);
        transport.Cells[4] = new byte[4];
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { VerificationReturnTimeoutMs = 0 });
        var result = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
        Assert.False(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Empty(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        Assert.NotNull(station.ReadCardInfo());
    }

    [Fact]
    public async Task FailedUnknownHeaderWriteStopsWithoutSuccessAndConnectionCanReadNextCard()
    {
        using var transport = new CardMemory { IgnoreWriteCell = 4 };
        transport.Cells[4] = [0xFF, 0xFF, 0xFF, 0xFF];
        using var station = new SfrStation(transport);
        var writer = new SfrNumberWriter(station);
        var result = await writer.AssignAsync(123);
        Assert.False(result.Success);
        Assert.True(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(4, Assert.Single(transport.Mutations)[4]);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);

        transport.Cells[4] = [6, 0, 0xF0, 0];
        var next = await writer.AssignAsync(321);
        Assert.True(next.Success);
        Assert.False(next.CardCleared);
        Assert.Equal(321, next.Card!.Number);
    }

    [Fact]
    public async Task StrictModeRejectionIncludesHeaderAndDoesNotBlockNextCard()
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [6, 1, 0, 0];
        using var station = new SfrStation(transport,
            options: new SfrStationOptions { FormatUnknownCards = false });
        var writer = new SfrNumberWriter(station);
        var error = await Assert.ThrowsAsync<SfrProtectedCardException>(() => writer.AssignAsync(123));
        Assert.Contains("06010000", error.Message);
        Assert.Empty(transport.Mutations);

        transport.Cells[4] = [6, 0, 0xF0, 0];
        var result = await writer.AssignAsync(123);
        Assert.True(result.Success);
        Assert.False(result.CardCleared);
        Assert.Equal(new byte[] { 3, 1 }, transport.Mutations.Select(r => r[4]));
    }

    [Fact]
    public void ObservedHidReplyKeepsParticipantMetadataInsidePayload()
    {
        var reply = Convert.FromHexString("3F09FD01040600F00000FE17F5D2CF9A71EDAEEF2B7AF575F9BF0BB17BFFCA771B2CA83D7325ACEFBFEA9BF3A3D777B7717BEB67D7F2AB2D6D57F161BFE5675B");
        var payload = SfrProtocol.ParseFrame(reply);
        Assert.Equal(new byte[] { 1, 4, 6, 0, 0xF0, 0 }, payload);
        Assert.Equal(SfrCardKind.Participant, SfrProtocol.ClassifyCard(payload.AsSpan(2)));
    }
}
