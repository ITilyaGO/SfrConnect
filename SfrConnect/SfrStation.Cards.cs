using System.Globalization;

namespace SfrConnect;

public sealed partial class SfrStation
{
    /// <summary>Reads the card kind before interpreting its number. Service numbers are not decoded.</summary>
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

    /// <summary>Clears punches and converts service cards. A service card without a valid number gets zero.</summary>
    public SfrCardNumber ClearCard() => ClearCardCore(null, null, default);

    /// <summary>Assigns a number, optionally clearing recorded punches in the same verified operation.</summary>
    public SfrCardNumber WriteCardNumber(int newNumber, bool clearCard) => clearCard
        ? ClearCardCore(newNumber, null, default) : WriteCardNumber(newNumber);

    internal SfrCardNumber ClearCardCore(int? newNumber, int? expectedNumber, CancellationToken cancellationToken)
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
            try
            {
                var marker = ReadCell(1) ?? throw new SfrCardNotReadyException("Чип недоступен. Очистка не началась.");
                var header = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран при чтении счётчика.");
                var kind = SfrProtocol.ClassifyCard(header);
                if (kind == SfrCardKind.Unknown) throw new SfrProtectedCardException(kind, header);
                bool service = kind == SfrCardKind.Service;
                var numberCell = ReadCell(3) ?? throw new SfrCardNotReadyException("Чип убран при чтении номера.");
                var snapshot = (Marker: marker, NumberCell: numberCell);
                int? currentNumber = null;
                try { currentNumber = SfrProtocol.DecodeCardNumber(numberCell); }
                catch (InvalidDataException) when (service) { }
                if (expectedNumber.HasValue && expectedNumber != currentNumber)
                    throw new InvalidOperationException("Текущий номер не совпадает с ожидаемым. Изменения не отправлены.");
                CheckMarker(marker);
                // SFR basic uses cells 00..7F. USB does not expose NFC system-info capacity here.
                if (!service && header[0] > 127)
                    throw new NotSupportedException("Очистка пока поддерживает формат SFR basic на 128 ячеек.");
                var plan = new List<(byte Cell, byte[] Data)>();
                // Neutralize the service mode first, before removing its command data.
                if (service) plan.Add((4, new byte[] { 5, 0, 0, 0 }));
                for (int cell = 2; cell <= 127; cell++)
                {
                    if (cell is 3 or 4) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    var data = ReadCell((byte)cell);
                    if (data is null)
                    {
                        CheckMarker(snapshot.Marker);
                        throw new NotSupportedException($"Ячейка {cell:X2} недоступна. Очистка требует формата на 128 ячеек.");
                    }
                    // Already empty cells need verification, but no additional write cycle.
                    if (data.Any(value => value != 0)) plan.Add(((byte)cell, new byte[4]));
                }
                int resultNumber = newNumber ?? (service && (currentNumber is null || currentNumber > MaximumWritableCardNumber) ? 0 : currentNumber!.Value);
                byte[] desiredNumber = newNumber.HasValue || service ? SfrProtocol.EncodeCardNumber(resultNumber) : snapshot.NumberCell;
                if (!desiredNumber.AsSpan().SequenceEqual(numberCell)) plan.Add((3, desiredNumber));
                byte[] clearedHeader = [5, 0, 0, 0];
                if (!service && !header.AsSpan().SequenceEqual(clearedHeader)) plan.Add((4, clearedHeader));
                var newMarker = Convert.FromHexString("00" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
                plan.Add((1, newMarker));
                CheckMarker(snapshot.Marker);
                var currentHeader = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран перед очисткой.");
                if (!currentHeader.AsSpan().SequenceEqual(header))
                    throw new SfrCardNotReadyException("Счётчик изменился перед очисткой.");

                var expectedHeader = header;
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
                    ReadWriteResponse();
                    // Confirm a mode change immediately, especially before erasing service commands.
                    // All other data is checked together in the final memory pass.
                    if (item.Cell == 4)
                    {
                        var readback = ReadCell(4);
                        if (readback is null || !readback.AsSpan().SequenceEqual(item.Data))
                            throw new InvalidDataException("Не подтверждён сброс заголовка после очистки.");
                        expectedHeader = item.Data;
                    }
                }
                // One final pass covers both changed cells and the previously empty tail.
                // Bracket this read-only pass instead of reading the marker before every cell.
                CheckMarker(newMarker);
                byte[] empty = new byte[4];
                for (int cell = 2; cell <= 127; cell++)
                {
                    var expected = cell == 3 ? desiredNumber : cell == 4 ? clearedHeader : empty;
                    var readback = ReadCell((byte)cell);
                    if (readback is null || !readback.AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException($"Итоговая проверка ячейки {cell:X2} не пройдена.");
                }
                var verified = ReadSnapshot(participantOnly: true)
                    ?? throw new IOException("Чип убран после очистки.");
                if (!verified.Marker.AsSpan().SequenceEqual(newMarker) || !verified.NumberCell.AsSpan().SequenceEqual(desiredNumber))
                    throw new InvalidDataException("Итоговая проверка номера после очистки не пройдена.");
                return new SfrCardNumber(resultNumber, Convert.ToHexString(newMarker));
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
