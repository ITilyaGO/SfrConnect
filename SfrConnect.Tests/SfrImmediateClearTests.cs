namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData("0600F000", 0x55)]
    [InlineData("05050505", 0x55)]
    [InlineData("FFFFFFFF", 0x55)]
    [InlineData("0600F000", 8)]
    public async Task DefaultClearVerifiesEveryWrittenCellBeforeNextWrite(string header, byte acknowledgement)
    {
        using var transport = new CardMemory { WriteReplyCode = acknowledgement };
        transport.Cells[4] = Convert.FromHexString(header);
        transport.Cells[126] = [31, 1, 2, 3];
        var logs = new List<string>();
        using var station = new SfrStation(transport, logs.Add);
        var result = await new SfrNumberWriter(station).AssignAsync(123, clearCard: true);
        Assert.True(result.Success);
        Assert.True(result.CardCleared);
        Assert.True(result.SignalSent);
        Assert.Equal(SfrClearMode.AfterEachWrite, result.AppliedClearMode);
        Assert.Equal((byte)127, result.ClearedThroughCell);
        Assert.Equal(123, result.Card!.Number);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));

        for (int index = 0; index < transport.Requests.Count; index++)
        {
            var write = transport.Requests[index];
            if (write[3] != 2) continue;
            var untilNextWrite = transport.Requests.Skip(index + 1).TakeWhile(r => r[3] != 2).ToArray();
            Assert.Contains(untilNextWrite, r => r[3] == 1 && r[4] == write[4]);
            if (acknowledgement == 0x55)
            {
                Assert.Equal(1, untilNextWrite[0][3]);
                Assert.Equal(write[4], untilNextWrite[0][4]);
            }
        }
        var afterFinalWrite = transport.Requests.Skip(transport.Requests.FindLastIndex(r => r[3] == 2) + 1).ToArray();
        Assert.DoesNotContain(afterFinalWrite, r => r[3] == 1 && (r[4] == 2 || r[4] >= 5));
        Assert.Contains(afterFinalWrite, r => r[3] == 1 && r[4] == 3);
        Assert.Contains(afterFinalWrite, r => r[3] == 1 && r[4] == 4);
        Assert.DoesNotContain(logs, line => line.Contains("итоговая проверка памяти"));
        Assert.Contains(logs, line => line.Contains("завершающая проверка номера, типа и маркера"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task AcknowledgedButUnchangedCellStopsBeforeNextWrite(byte cell)
    {
        using var transport = new CardMemory { IgnoreWriteCell = cell };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(123, clearCard: true);
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.Equal(cell, result.LastAttemptedCell);
        Assert.True(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(cell, transport.Mutations[^1][4]);
        Assert.Contains(cell.ToString("X2"), result.Error);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        Assert.NotNull(station.ReadCardInfo());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingReadbackWaitsWithoutResendingOrAdvancing(bool returns)
    {
        var clock = new MaintenanceClock();
        bool lost = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!lost && memory.Mutations.Count == 1 && request[3] == 1 && request[4] == 2)
                { lost = true; memory.Present = false; }
            }
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            Assert.Single(transport.Mutations);
            clock.Advance(delay);
            if (returns && clock.Milliseconds >= 4200) transport.Present = true;
        });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(lost);
        Assert.Equal(returns, result.Success);
        Assert.Equal(returns, result.SignalSent);
        Assert.Equal(returns ? 4200 : 5000, clock.Milliseconds);
        Assert.False(result.RequiresReconnect);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
        if (!returns)
        {
            Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
            Assert.Single(transport.Mutations);
            transport.Present = true;
            Assert.NotNull(station.ReadCardInfo());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FinalIdentityAndNumberCheckRejectsChanges(byte cell)
    {
        int markerReads = 0;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 1 && ++markerReads == 2)
                    memory.Cells[cell][0] ^= 1;
            }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.False(result.RequiresReconnect);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalNumberCheckWaitsForReturnOrReportsUnverified(bool returns)
    {
        var clock = new MaintenanceClock();
        bool lost = false;
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (!lost && memory.Mutations.Any(r => r[4] == 1) && request[3] == 1 && request[4] == 3)
                { lost = true; memory.Present = false; }
            }
        };
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            if (returns && clock.Milliseconds >= 4200) transport.Present = true;
        });
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.True(lost);
        Assert.Equal(returns, result.Success);
        Assert.Equal(returns, result.SignalSent);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(returns ? 4200 : 5000, clock.Milliseconds);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
    }

    [Theory]
    [InlineData("empty", 138)]
    [InlineData("sparse", 158)]
    [InlineData("full", 638)]
    public void ImmediateChecksReduceExchangesWithoutRewritingEmptyCells(string layout, int expectedRequests)
    {
        using var transport = new CardMemory();
        if (layout == "empty")
        {
            transport.Cells[2] = new byte[4];
            transport.Cells[4] = [5, 0, 0, 0];
            foreach (var cell in transport.Cells.Skip(5)) Array.Clear(cell);
        }
        else if (layout == "sparse") transport.Cells[126] = [31, 1, 2, 3];
        else foreach (var cell in transport.Cells.Skip(5)) cell[0] = 31;
        using var station = new SfrStation(transport);
        station.ClearCard();
        Assert.Equal(expectedRequests, transport.Requests.Count);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
    }
}
