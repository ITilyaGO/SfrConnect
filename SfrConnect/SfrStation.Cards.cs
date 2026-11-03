using System.Globalization;

namespace SfrConnect;

public sealed partial class SfrStation
{
    /// <summary>Reads the card kind before interpreting its number. Non-participant numbers are not decoded.</summary>
    public SfrCardInfo? ReadCardInfo()
    {
        lock (sync)
        {
            EnsureExchangeAvailable();
            try
            {
                var marker = ReadCell(1);
                if (marker is null) return null;
                var header = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран при проверке типа.");
                var kind = SfrProtocol.ClassifyCard(header);
                int? number = kind == SfrCardKind.Participant
                    ? SfrProtocol.DecodeCardNumber(ReadCell(3) ?? throw new SfrCardNotReadyException("Чип убран при чтении номера."))
                    : null;
                CheckMarker(marker);
                return new SfrCardInfo(kind, number, Convert.ToHexString(marker), Convert.ToHexString(header));
            }
            catch (SfrCardNotReadyException) { throw; }
            catch { exchangeFaulted = true; throw; }
        }
    }

    /// <summary>Clears punches and normalizes service/allowed unknown cards. A normalized card without a valid number gets zero.</summary>
    public SfrCardNumber ClearCard() => ClearCardCore(null, null, default);

    /// <summary>Assigns a number, optionally clearing recorded punches in the same verified operation.</summary>
    public SfrCardNumber WriteCardNumber(int newNumber, bool clearCard) => clearCard
        ? ClearCardCore(newNumber, null, default) : WriteCardNumber(newNumber);

    internal SfrCardNumber ClearCardCore(int? newNumber, int? expectedNumber, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            var result = TryClearCardCore(newNumber, expectedNumber, cancellationToken);
            if (result.Success) return result.Card;
            if (!result.CardMayHaveChanged) throw new SfrCardNotReadyException(result.Error ?? "Подготовка очистки не завершена.");
            exchangeFaulted = true;
            throw new SfrCardUpdateUnverifiedException(newNumber, result.LastAttemptedCell ?? 0,
                new InvalidDataException(result.Error));
        }
    }

    internal SfrNumberAssignmentResult TryClearCardCore(int? newNumber, int? expectedNumber, CancellationToken cancellationToken)
    {
        if (newNumber.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(newNumber.Value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(newNumber.Value, MaximumWritableCardNumber);
        }
        lock (sync)
        {
            EnsureExchangeAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            bool attempted = false;
            byte lastCell = 0;
            using var timing = MeasureStage("Очистка: определение типа и номера");
            try
            {
                var marker = ReadCell(1) ?? throw new SfrCardNotReadyException("Чип недоступен. Очистка не началась.");
                var header = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран при чтении счётчика.");
                var kind = SfrProtocol.ClassifyCard(header);
                if (kind == SfrCardKind.Unknown && !FormatUnknownCards) throw new SfrProtectedCardException(kind, header);
                bool normalize = kind != SfrCardKind.Participant;
                if (normalize)
                    log?.Invoke($"Приведение чипа к обычному формату: тип {kind}, заголовок {Convert.ToHexString(header)}. Будут очищены отметки в ячейках 02–7F.");
                var numberCell = ReadCell(3) ?? throw new SfrCardNotReadyException("Чип убран при чтении номера.");
                var snapshot = (Marker: marker, NumberCell: numberCell);
                int? currentNumber = null;
                try { currentNumber = SfrProtocol.DecodeCardNumber(numberCell); }
                catch (InvalidDataException) when (normalize) { }
                if (expectedNumber.HasValue && expectedNumber != currentNumber)
                    throw new InvalidOperationException("Текущий номер не совпадает с ожидаемым. Изменения не отправлены.");
                CheckMarker(marker);
                // SFR basic uses cells 00..7F. USB does not expose NFC system-info capacity here.
                if (!normalize && header[0] > 127)
                    return SfrNumberAssignmentResult.PreparationFailed(newNumber,
                        "Счётчик выходит за поддерживаемые 128 ячеек. Очистка не началась.");
                var plan = new List<(byte Cell, byte[] Data)>();
                byte lastOccupiedCell = 5;
                timing.Next("Очистка: чтение памяти и подготовка");
                bool retrying = false;
                var preparationError = VerifyWithRetries(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (retrying)
                    {
                        var identity = CheckClearPreparationIdentity(marker, header);
                        if (identity.Error is not null) return identity;
                    }
                    retrying = true;
                    // Do not reuse a partial scan after contact is lost.
                    plan.Clear();
                    lastOccupiedCell = 5;
                    if (normalize) plan.Add((4, new byte[] { 5, 0, 0, 0 }));
                    for (int cell = 2; cell <= 127; cell++)
                    {
                        if (cell is 3 or 4) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        var data = ReadCell((byte)cell);
                        if (data is null)
                            return (true, (string?)$"Не удалось прочитать ячейку {cell:X2} при подготовке очистки. Изменения не отправлены.");
                        if (data.Any(value => value != 0))
                        {
                            plan.Add(((byte)cell, new byte[4]));
                            lastOccupiedCell = (byte)Math.Max(lastOccupiedCell, cell);
                        }
                    }
                    return CheckClearPreparationIdentity(marker, header);
                }, "Очистка: подготовка", cancellationToken);
                if (preparationError is not null)
                    return SfrNumberAssignmentResult.PreparationFailed(newNumber, preparationError);
                // Keep the boundary from the completed full scan; cell 04 is not the source of this limit.
                byte lastClearCell = !normalize && clearMode == SfrClearMode.ThroughLastNonEmptyCell
                    ? lastOccupiedCell : (byte)127;
                var appliedMode = normalize && clearMode == SfrClearMode.ThroughLastNonEmptyCell
                    ? SfrClearMode.FullMemory : clearMode;
                log?.Invoke(appliedMode == SfrClearMode.AfterEachWrite
                    ? "ЭТАП · Очистка: проверка каждой ячейки сразу после записи; в конце — номер, тип и маркер, без повторного прохода памяти."
                    : $"ЭТАП · Очистка: диапазон итоговой проверки 02–{lastClearCell:X2} · " +
                    (lastClearCell < 127 ? "по последней непустой ячейке; пустой хвост прочитан при подготовке и повторно не проверяется."
                        : "полный поддерживаемый диапазон."));
                int resultNumber = newNumber ?? (normalize && (currentNumber is null || currentNumber > MaximumWritableCardNumber) ? 0 : currentNumber!.Value);
                byte[] desiredNumber = newNumber.HasValue || normalize ? SfrProtocol.EncodeCardNumber(resultNumber) : snapshot.NumberCell;
                if (!desiredNumber.AsSpan().SequenceEqual(numberCell)) plan.Add((3, desiredNumber));
                byte[] clearedHeader = [5, 0, 0, 0];
                if (!normalize && !header.AsSpan().SequenceEqual(clearedHeader)) plan.Add((4, clearedHeader));
                var newMarker = Convert.FromHexString("00" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
                plan.Add((1, newMarker));

                var expectedHeader = header;
                timing.Next($"Очистка: запись {plan.Count} ячеек" + (appliedMode == SfrClearMode.AfterEachWrite
                    ? " с немедленной проверкой каждой" : " с проверками перед записью"));
                foreach (var item in plan)
                {
                    // Cancellation after the first attempted mutation must not hide a partial operation.
                    if (!attempted) cancellationToken.ThrowIfCancellationRequested();
                    CheckMarker(snapshot.Marker);
                    var activeHeader = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран во время очистки.");
                    if (!activeHeader.AsSpan().SequenceEqual(expectedHeader))
                        throw new SfrCardNotReadyException("Счётчик изменился во время очистки.");
                    lastCell = item.Cell;
                    var report = SfrProtocol.CreateWriteReport(item.Cell, item.Data);
                    log?.Invoke($"Очистка: запись ячейки {item.Cell:X2}.");
                    log?.Invoke($"TX попытка ({report.Length} байт): {Convert.ToHexString(report)}");
                    if (!attempted) cancellationToken.ThrowIfCancellationRequested();
                    attempted = true;
                    stream.Write(report, 0, report.Length);
                    var acknowledged = ReadWriteResponse(allowUnconfirmedReadback: true);
                    if (!acknowledged)
                    {
                        log?.Invoke($"ЭТАП · Очистка: проверка ячейки {item.Cell:X2} · ответ 2A 08; проверяем чтением, запись не повторяется.");
                        var error = VerifyClearCell(item.Cell, item.Data,
                            item.Cell == 1 ? newMarker : snapshot.Marker,
                            item.Cell == 4 ? item.Data : expectedHeader);
                        if (error is not null)
                            return SfrNumberAssignmentResult.Unverified(newNumber,
                                $"Ответ записи 2A 08, ячейка {item.Cell:X2}. {error}", item.Cell, clearing: true);
                    }
                    // A positive acknowledgement is not proof that memory changed.
                    // On contact loss, retry only readback, with the expected identity and header.
                    if (acknowledged && (appliedMode == SfrClearMode.AfterEachWrite || item.Cell == 4))
                    {
                        var readback = ReadCell(item.Cell);
                        var error = readback is null
                            ? VerifyClearCell(item.Cell, item.Data,
                                item.Cell == 1 ? newMarker : snapshot.Marker,
                                item.Cell == 4 ? item.Data : expectedHeader)
                            : readback.AsSpan().SequenceEqual(item.Data) ? null
                                : $"Проверка ячейки {item.Cell:X2} сразу после записи не пройдена: данные не совпадают.";
                        if (error is not null)
                            return SfrNumberAssignmentResult.Unverified(newNumber, error, item.Cell, clearing: true);
                    }
                    if (item.Cell == 4) expectedHeader = item.Data;
                }
                // Restart the whole read-only pass after a loss, so success never combines partial reads.
                var verificationError = VerifyWithRetries(() =>
                {
                    timing.Next(appliedMode == SfrClearMode.AfterEachWrite
                        ? "Очистка: завершающая проверка номера, типа и маркера"
                        : $"Очистка: итоговая проверка памяти 02–{lastClearCell:X2}");
                    var check = appliedMode == SfrClearMode.AfterEachWrite
                        ? CheckWrittenNumber(newMarker, desiredNumber, clearedHeader)
                        : CheckClearedMemory(newMarker, desiredNumber, clearedHeader, lastClearCell,
                            () => timing.Next("Очистка: завершающая проверка номера и типа"));
                    if (check.Error is not null) timing.Finish(success: false);
                    return check;
                }, "Очистка: итоговая проверка");
                if (verificationError is not null)
                    return SfrNumberAssignmentResult.Unverified(newNumber, verificationError, lastCell, clearing: true);
                timing.Finish();
                return new(new SfrCardNumber(resultNumber, Convert.ToHexString(newMarker)), false, null)
                { CardCleared = true, ClearedThroughCell = lastClearCell, AppliedClearMode = appliedMode };
            }
            catch (SfrCardNotReadyException) when (!attempted) { throw; }
            catch (SfrProtectedCardException) when (!attempted) { throw; }
            catch (OperationCanceledException) when (!attempted) { throw; }
            catch (Exception error)
            {
                exchangeFaulted = true;
                if (attempted) throw new SfrCardUpdateUnverifiedException(newNumber, lastCell, error);
                throw;
            }
        }
    }

    private (bool Unavailable, string? Error) CheckClearPreparationIdentity(byte[] marker, byte[] header)
    {
        var actualMarker = ReadCell(1);
        if (actualMarker is null) return (true, "Чип недоступен при подготовке очистки. Изменения не отправлены.");
        if (!actualMarker.AsSpan().SequenceEqual(marker)) return (false, "Маркер изменился при подготовке очистки. Изменения не отправлены.");
        var actualHeader = ReadCell(4);
        if (actualHeader is null) return (true, "Чип недоступен при проверке заголовка. Изменения не отправлены.");
        return actualHeader.AsSpan().SequenceEqual(header)
            ? (false, null) : (false, "Заголовок изменился при подготовке очистки. Изменения не отправлены.");
    }

    private (bool Unavailable, string? Error) CheckClearedMemory(byte[] marker, byte[] number,
        byte[] header, byte lastClearCell, Action beforeNumberCheck)
    {
        var firstMarker = ReadCell(1);
        if (firstMarker is null) return (true, "Чип недоступен перед итоговой проверкой очистки.");
        if (!firstMarker.AsSpan().SequenceEqual(marker)) return (false, "Маркер чипа изменился после очистки.");
        byte[] empty = new byte[4];
        for (int cell = 2; cell <= lastClearCell; cell++)
        {
            var expected = cell == 3 ? number : cell == 4 ? header : empty;
            var readback = ReadCell((byte)cell);
            if (readback is null) return (true, $"Чип недоступен при итоговой проверке ячейки {cell:X2}.");
            if (!readback.AsSpan().SequenceEqual(expected))
                return (false, $"Итоговая проверка ячейки {cell:X2} не пройдена: данные не совпадают.");
        }
        beforeNumberCheck();
        return CheckWrittenNumber(marker, number, header);
    }

    private string? VerifyClearCell(byte cell, byte[] desired, byte[] expectedMarker, byte[] expectedHeader)
        => VerifyWithRetries(() =>
        {
            var unavailable = (true, (string?)$"Чип недоступен при проверке ячейки {cell:X2} после очистки.");
            var marker = ReadCell(1);
            if (marker is null) return unavailable;
            if (!marker.AsSpan().SequenceEqual(expectedMarker)) return (false, "Маркер чипа изменился при очистке.");
            var header = ReadCell(4);
            if (header is null) return unavailable;
            if (!header.AsSpan().SequenceEqual(expectedHeader)) return (false, "Заголовок чипа не совпадает с ожидаемым после очистки.");
            var data = cell == 4 ? header : cell == 1 ? marker : ReadCell(cell);
            if (data is null) return unavailable;
            if (!data.AsSpan().SequenceEqual(desired)) return (false, $"Ячейка {cell:X2} не совпадает с записываемыми данными.");
            var finalMarker = ReadCell(1);
            if (finalMarker is null) return unavailable;
            return finalMarker.AsSpan().SequenceEqual(expectedMarker)
                ? (false, (string?)null) : (false, "Маркер чипа изменился во время проверки очистки.");
        }, $"Очистка: проверка ячейки {cell:X2}");

    private void CheckMarker(byte[] expected)
    {
        var actual = ReadCell(1);
        if (actual is null || !actual.AsSpan().SequenceEqual(expected))
            throw new SfrCardNotReadyException("Чип убран или заменён во время операции.");
    }

    private static void EnsureParticipant(byte[] header)
    {
        var kind = SfrProtocol.ClassifyCard(header);
        if (kind != SfrCardKind.Participant) throw new SfrProtectedCardException(kind, header);
    }
}
