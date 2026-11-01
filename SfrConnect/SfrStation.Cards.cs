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

    /// <summary>Clears recorded punches and resets their counter, preserving the number.</summary>
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
                var snapshot = ReadSnapshot(participantOnly: true)
                    ?? throw new SfrCardNotReadyException("Чип недоступен. Очистка не началась.");
                int currentNumber = SfrProtocol.DecodeCardNumber(snapshot.NumberCell);
                if (expectedNumber.HasValue && expectedNumber != currentNumber)
                    throw new SfrCardNotReadyException("Чип изменился до очистки.");
                var header = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран при чтении счётчика.");
                EnsureParticipant(header);
                // SFR basic uses cells 00..7F. USB does not expose NFC system-info capacity here.
                if (header[0] > 127)
                    throw new NotSupportedException("Очистка пока поддерживает формат SFR basic на 128 ячеек.");
                var plan = new List<(byte Cell, byte[] Data)>();
                int lastNonzero = 5;
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
                    if (cell >= 5 && data.Any(value => value != 0)) lastNonzero = cell;
                }
                // Like SFR phone registration, clear through the last nonzero punch, including stale records.
                plan.Add((2, new byte[4]));
                for (int cell = 5; cell <= lastNonzero; cell++) plan.Add(((byte)cell, new byte[4]));
                byte[] desiredNumber = newNumber.HasValue ? SfrProtocol.EncodeCardNumber(newNumber.Value) : snapshot.NumberCell;
                if (newNumber.HasValue) plan.Add((3, desiredNumber));
                plan.Add((4, new byte[] { 5, 0, 0, 0 }));
                var newMarker = Convert.FromHexString("00" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
                plan.Add((1, newMarker));
                CheckMarker(snapshot.Marker);
                var currentHeader = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран перед очисткой.");
                EnsureParticipant(currentHeader);
                if (!currentHeader.AsSpan().SequenceEqual(header))
                    throw new SfrCardNotReadyException("Счётчик изменился перед очисткой.");

                var expectedHeader = header;
                foreach (var item in plan)
                {
                    // Cancellation after the first attempted mutation must not hide a partial operation.
                    if (!attempted) cancellationToken.ThrowIfCancellationRequested();
                    CheckMarker(snapshot.Marker);
                    var activeHeader = ReadCell(4) ?? throw new SfrCardNotReadyException("Чип убран во время очистки.");
                    EnsureParticipant(activeHeader);
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
                    var readback = ReadCell(item.Cell);
                    if (readback is null || !readback.AsSpan().SequenceEqual(item.Data))
                        throw new InvalidDataException($"Не подтверждена ячейка {item.Cell:X2} после очистки.");
                    if (item.Cell == 4) expectedHeader = item.Data;
                }
                for (int cell = lastNonzero + 1; cell <= 127; cell++)
                {
                    CheckMarker(newMarker);
                    var tail = ReadCell((byte)cell);
                    if (tail is null || tail.Any(value => value != 0))
                        throw new InvalidDataException($"Отметки в ячейке {cell:X2} не очищены.");
                }
                // Re-read all changed cells at the end, so per-cell success is not mistaken for final success.
                foreach (var item in plan)
                {
                    CheckMarker(newMarker);
                    var readback = ReadCell(item.Cell);
                    if (readback is null || !readback.AsSpan().SequenceEqual(item.Data))
                        throw new InvalidDataException($"Итоговая проверка ячейки {item.Cell:X2} не пройдена.");
                }
                var verified = ReadSnapshot(participantOnly: true)
                    ?? throw new IOException("Чип убран после очистки.");
                if (!verified.Marker.AsSpan().SequenceEqual(newMarker) || !verified.NumberCell.AsSpan().SequenceEqual(desiredNumber))
                    throw new InvalidDataException("Итоговая проверка номера после очистки не пройдена.");
                return new SfrCardNumber(newNumber ?? currentNumber, Convert.ToHexString(newMarker));
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
