namespace SfrConnect.Tests;

public partial class SfrCardMaintenanceTests
{
    [Theory]
    [InlineData("empty", 264, 1)]
    [InlineData("sparse", 280, 6)]
    [InlineData("full", 640, 126)]
    public void ClearUsesFewerUsbExchangesAndVerifiesEveryDataCell(string layout, int requestBudget, int writes)
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

        Assert.InRange(transport.Requests.Count, 1, requestBudget);
        Assert.Equal(writes, transport.Mutations.Count);
        var finalReads = transport.Requests.Skip(transport.Requests.FindLastIndex(r => r[3] == 2) + 1);
        for (int cell = 2; cell <= 127; cell++)
            Assert.Contains(finalReads, r => r[3] == 1 && r[4] == cell);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
    }

    [Fact]
    public async Task AcknowledgedButUnchangedPunchPreventsSuccessSignal()
    {
        using var transport = new CardMemory { IgnoreWriteCell = 5 };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        Assert.Equal(241, transport.Cells[5][0]);
    }

    [Fact]
    public void FailedServiceHeaderVerificationStopsBeforeErasingCommands()
    {
        using var transport = new CardMemory { IgnoreWriteCell = 4 };
        transport.Cells[4] = [0, 1, 0, 0];
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardUpdateUnverifiedException>(() => station.ClearCard());
        Assert.Equal(4, Assert.Single(transport.Mutations)[4]);
        Assert.Equal(241, transport.Cells[5][0]);
    }

    [Fact]
    public async Task CardChangedDuringFinalReadOnlyPassPreventsSuccessSignal()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (memory.Mutations.Count > 0 && request[3] == 1 && request[4] == 100)
                    memory.Cells[1][0] ^= 1;
            }
        };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Fact]
    public void PreviouslyVerifiedWriteCorruptedLaterIsDetectedByFinalPass()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) =>
            {
                if (request[3] == 2 && request[4] == 1) memory.Cells[2] = [1, 2, 3, 4];
            }
        };
        using var station = new SfrStation(transport);
        Assert.Throws<SfrCardUpdateUnverifiedException>(() => station.ClearCard());
    }

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
    [InlineData("0600F000", SfrCardKind.Participant)]
    [InlineData("0500F1A5", SfrCardKind.Participant)]
    [InlineData("00000000", SfrCardKind.Unknown)]
    [InlineData("04000000", SfrCardKind.Unknown)]
    [InlineData("06010000", SfrCardKind.Unknown)]
    public void ClassifiesKnownHeadersAndProtectsUnknownLayouts(string hex, SfrCardKind expected)
        => Assert.Equal(expected, SfrProtocol.ClassifyCard(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("00000000")]
    [InlineData("06010000")]
    public async Task UnknownCardsCannotBeChangedWhenFormattingIsDisabled(string header)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = Convert.FromHexString(header);
        transport.Cells[3] = [255, 255, 255, 255];
        using var station = new SfrStation(transport, options: new SfrStationOptions { FormatUnknownCards = false });
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
        var logs = new List<string>();
        using var station = new SfrStation(transport, logs.Add);
        var result = await new SfrNumberWriter(station).AssignAsync(123, clearCard: true);
        Assert.Equal(123, result.Card!.Number);
        Assert.True(result.CardCleared);
        Assert.True(result.SignalSent);
        Assert.Equal(new byte[] { 123, 0, 0, 0 }, transport.Cells[3]);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
        Assert.Equal(3, transport.Requests[^1][3]);
        var completed = logs.Where(m => m.StartsWith("ЭТАП · ") && m.Contains(" · готово · ")).ToArray();
        Assert.Equal(8, completed.Length);
        Assert.Contains("Ожидание и обнаружение чипа", completed[0]);
        Assert.Contains("определение типа и номера", completed[1]);
        Assert.Contains("чтение памяти", completed[2]);
        Assert.Contains("запись", completed[3]);
        Assert.Contains("итоговая проверка памяти", completed[4]);
        Assert.Contains("завершающая проверка", completed[5]);
        Assert.Contains("Сигнал", completed[6]);
        Assert.Contains("ВСЯ КОМАНДА", completed[7]);
        Assert.DoesNotContain(logs, m => m.Contains("прервано"));
    }

    [Fact]
    public async Task StandaloneAsyncClearPreservesNumber()
    {
        using var transport = new CardMemory();
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.Equal(2, result.Card!.Number);
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
        Assert.Equal(3, result.Card!.Number);
    }

    [Fact]
    public async Task LostAckAfterFirstMutationReportsPartialOperationWithoutRetry()
    {
        using var transport = new CardMemory { LoseWriteAck = true };
        var logs = new List<string>();
        using var station = new SfrStation(transport, logs.Add);
        var error = await new SfrNumberWriter(station).AssignAsync(3, clearCard: true);
        Assert.False(error.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, error.Failure);
        Assert.Equal(3, error.RequestedNumber);
        Assert.Equal((byte)2, error.LastAttemptedCell);
        Assert.Single(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, request => request[3] == 3);
        Assert.Throws<InvalidOperationException>(() => station.ReadCardNumber());
        Assert.Contains(logs, m => m.Contains("Очистка: запись") && m.Contains("прервано"));
        Assert.Contains(logs, m => m.Contains("ВСЯ КОМАНДА") && m.Contains("прервано"));
        Assert.DoesNotContain(logs, m => m.Contains("ВСЯ КОМАНДА") && m.Contains("готово"));
    }

    [Fact]
    public async Task UnconfirmedWriteReplyDuringClearStopsFurtherMutations()
    {
        using var transport = new CardMemory { WriteReplyCode = 8, IgnoreWriteCell = 2 };
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.False(result.RequiresReconnect);
        Assert.Single(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearCanVerifyEvery2A08ReplyWithoutRepeatingWrites(bool service)
    {
        using var transport = new CardMemory { WriteReplyCode = 8 };
        if (service) transport.Cells[4] = [5, 5, 5, 5];
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(45, clearCard: true);
        Assert.True(result.Success);
        Assert.True(result.SignalSent);
        Assert.True(result.CardCleared);
        Assert.Equal(45, result.Card!.Number);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
        Assert.Equal(SfrCardKind.Participant, station.ReadCardInfo()!.Kind);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0x55)]
    public async Task ServiceCardReturnsAfterHeaderWriteAndClearContinuesOnlyAfterReadback(byte reply)
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            WriteReplyCode = reply,
            OnRequest = (memory, request) =>
            {
                if (request[3] == 2 && request[4] == 4) memory.Present = false;
            }
        };
        transport.Cells[4] = [5, 5, 5, 5];
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            Assert.Single(transport.Mutations);
            clock.Advance(delay);
            if (clock.Milliseconds >= 4200) transport.Present = true;
        });
        var result = await new SfrNumberWriter(station).AssignAsync(45);
        Assert.True(result.Success);
        Assert.True(result.SignalSent);
        Assert.Equal(4200, clock.Milliseconds);
        Assert.Equal(4, transport.Mutations[0][4]);
        Assert.All(transport.Mutations.GroupBy(r => r[4]), group => Assert.Single(group));
    }

    [Fact]
    public async Task AbsentServiceCardAfter2A08ReturnsNormalFailureAtDeadline()
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            WriteReplyCode = 8,
            OnRequest = (memory, request) => { if (request[3] == 2) memory.Present = false; }
        };
        transport.Cells[4] = [5, 5, 5, 5];
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: clock.Advance);
        var result = await new SfrNumberWriter(station).AssignAsync(45);
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, result.Failure);
        Assert.Equal((byte)4, result.LastAttemptedCell);
        Assert.Equal(5000, clock.Milliseconds);
        Assert.Single(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
        transport.Present = true;
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public async Task UnchangedServiceHeaderAfter2A08StopsWithoutErasingCommands()
    {
        using var transport = new CardMemory { WriteReplyCode = 8, IgnoreWriteCell = 4 };
        transport.Cells[4] = [5, 5, 5, 5];
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(45);
        Assert.False(result.Success);
        Assert.False(result.RequiresReconnect);
        Assert.Single(transport.Mutations);
        Assert.Equal(241, transport.Cells[5][0]);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Fact]
    public async Task DifferentMarkerOnReturnStopsClearWithoutFurtherWrites()
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory
        {
            WriteReplyCode = 8,
            OnRequest = (memory, request) => { if (request[3] == 2) memory.Present = false; }
        };
        transport.Cells[4] = [5, 5, 5, 5];
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: delay =>
        {
            clock.Advance(delay);
            transport.Present = true;
            transport.Cells[1][0] ^= 1;
        });
        var result = await new SfrNumberWriter(station).AssignAsync(45);
        Assert.False(result.Success);
        Assert.Single(transport.Mutations);
        Assert.Contains("Маркер", result.Error);
        Assert.DoesNotContain(transport.Requests, r => r[3] == 3);
    }

    [Fact]
    public async Task ServiceCardAppearingMidClearStopsFurtherWrites()
    {
        using var transport = new CardMemory
        {
            OnRequest = (memory, request) => { if (request[3] == 2) memory.Cells[4] = [5, 5, 5, 5]; }
        };
        using var station = new SfrStation(transport);
        var error = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(error.Success);
        Assert.Contains("Счётчик изменился", error.Error);
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
    public async Task UnreadableMemoryReturnsPreparationFailureBeforeAnyMutation()
    {
        var clock = new MaintenanceClock();
        using var transport = new CardMemory(32);
        using var station = new SfrStation(transport, verificationClock: clock, verificationDelay: clock.Advance);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.False(result.Success);
        Assert.Equal(SfrAssignmentFailure.PreparationFailed, result.Failure);
        Assert.False(result.CardMayHaveChanged);
        Assert.False(result.RequiresReconnect);
        Assert.Null(result.LastAttemptedCell);
        Assert.Equal(5000, clock.Milliseconds);
        Assert.Empty(transport.Mutations);
        Assert.Equal(2, station.ReadCardNumber()!.Number);
    }

    [Fact]
    public void WriteFrameUsesTargetCellInChecksum()
    {
        var report = SfrProtocol.CreateWriteReport(127, [0, 0, 0, 0]);
        Assert.Equal(new byte[] { 0x3F, 9, 0xFD, 2, 127, 0, 0, 0, 0, 129, 0xFE }, report[..11]);
    }

    [Theory]
    [InlineData("00010000")]
    [InlineData("01020000")]
    [InlineData("02020000")]
    [InlineData("03020000")]
    [InlineData("05020000")]
    [InlineData("09020000")]
    [InlineData("01091234")]
    [InlineData("02091234")]
    [InlineData("03030303")]
    [InlineData("05050505")]
    public async Task AssignConvertsEveryKnownServiceModeEvenWithoutClearFlag(string header)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = Convert.FromHexString(header);
        transport.Cells[3] = [255, 255, 255, 255];
        transport.Cells[127] = [99, 1, 2, 3];
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).AssignAsync(123, clearCard: false);
        Assert.True(result.CardCleared);
        Assert.True(result.SignalSent);
        Assert.Equal(123, result.Card!.Number);
        Assert.Equal(4, transport.Mutations[0][4]);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, transport.Mutations[0][5..9]);
        Assert.Equal(SfrCardKind.Participant, station.ReadCardInfo()!.Kind);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
        Assert.Equal(new byte[4], transport.Cells[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllImmediateWriteOverloadsConvertServiceCards(int method)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [0, 1, 0, 0];
        using var station = new SfrStation(transport);
        var result = method switch
        {
            0 => station.WriteCardNumber(123),
            1 => station.WriteCardNumber(2, 123),
            2 => station.WriteCardNumber(123, clearCard: false),
            _ => station.WriteCardNumber(123, clearCard: true)
        };
        Assert.Equal(123, result.Number);
        Assert.Equal(SfrCardKind.Participant, station.ReadCardInfo()!.Kind);
        Assert.All(transport.Cells.Skip(5), cell => Assert.Equal(new byte[4], cell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearServiceCardPreservesValidNumberOrUsesZero(bool invalidNumber)
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [5, 5, 5, 5];
        if (invalidNumber) transport.Cells[3] = [255, 255, 255, 255];
        using var station = new SfrStation(transport);
        var result = await new SfrNumberWriter(station).ClearAsync();
        Assert.Equal(invalidNumber ? 0 : 2, result.Card!.Number);
        Assert.True(result.CardCleared);
        Assert.Equal(SfrCardKind.Participant, station.ReadCardInfo()!.Kind);
    }

    [Fact]
    public void GuardedConversionChecksExpectedNumberBeforeMutation()
    {
        using var transport = new CardMemory();
        transport.Cells[4] = [0, 1, 0, 0];
        using var station = new SfrStation(transport);
        Assert.Throws<InvalidOperationException>(() => station.WriteCardNumber(999, 123));
        Assert.Empty(transport.Mutations);
    }

    [Fact]
    public async Task FailedServiceConversionIsNotReportedOrSignaledAsSuccess()
    {
        using var transport = new CardMemory { LoseWriteAck = true };
        transport.Cells[4] = [0, 1, 0, 0];
        using var station = new SfrStation(transport);
        var error = await new SfrNumberWriter(station).AssignAsync(123);
        Assert.False(error.Success);
        Assert.Equal(SfrAssignmentFailure.ClearUnverified, error.Failure);
        Assert.Equal((byte)4, error.LastAttemptedCell);
        Assert.Single(transport.Mutations);
        Assert.DoesNotContain(transport.Requests, request => request[3] == 3);
    }

    private sealed class MaintenanceClock : TimeProvider
    {
        public long Milliseconds { get; private set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public void Advance(TimeSpan delay) => Milliseconds += (long)delay.TotalMilliseconds;
    }

    private sealed class CardMemory : MemoryStream
    {
        private readonly Queue<byte[]> replies = new();
        public byte[][] Cells { get; }
        public List<byte[]> Requests { get; } = [];
        public List<byte[]> Mutations { get; } = [];
        public Action<CardMemory, byte[]>? OnRequest { get; init; }
        public bool LoseWriteAck { get; init; }
        public byte WriteReplyCode { get; init; } = 0x55;
        public bool Present { get; set; } = true;
        public byte? IgnoreWriteCell { get; init; }
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
                replies.Enqueue(!Present || address >= Cells.Length ? Frame(0x2A, 6) : Frame(new byte[] { 1, address }.Concat(Cells[address]).ToArray()));
            }
            else if (request[3] == 2)
            {
                Mutations.Add(request);
                if (address != IgnoreWriteCell) Cells[address] = request[5..9];
                if (!LoseWriteAck) replies.Enqueue(Frame(0x2A, WriteReplyCode));
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
