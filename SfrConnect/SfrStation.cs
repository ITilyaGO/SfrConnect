using System.Diagnostics;
using HidSharp;

namespace SfrConnect;

/// <summary>A synchronous connection to the SFR USB HID readout station.</summary>
public sealed class SfrStation : IDisposable
{
    public const int VendorId = 0x2047;
    public const int ProductId = 0x0301;
    public const int MaximumWritableCardNumber = 39999;
    private readonly Stream stream;
    private readonly Action<string>? log;
    private readonly object sync = new();
    private bool disposed;
    private bool exchangeFaulted;

    internal SfrStation(Stream stream, Action<string>? log = null)
    {
        this.stream = stream;
        this.log = log;
    }

    /// <summary>Lists matching interfaces without opening a communication stream.</summary>
    public static IReadOnlyList<SfrDeviceInfo> FindDevices(bool includeOtherHidDevices = false)
    {
        EnsureWindows();
        var devices = includeOtherHidDevices
            ? DeviceList.Local.GetHidDevices()
            : DeviceList.Local.GetHidDevices(VendorId, ProductId);
        return devices.OrderBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
            .Select(Inspect).ToArray();
    }

    /// <summary>Opens one explicitly selected interface. Does not send any commands.</summary>
    public static SfrStation Open(string devicePath, Action<string>? log = null)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        var device = DeviceList.Local.GetHidDevices(VendorId, ProductId)
            .SingleOrDefault(device =>
                string.Equals(device.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Выбранная станция SFR не найдена. Подключите её и повторите поиск.");

        var reports = device.GetReportDescriptor().OutputReports;
        log?.Invoke("Выходные HID-отчёты: " + string.Join(", ",
            reports.Select(report => $"ID=0x{report.ReportID:X2}, длина={report.Length}")));
        SfrProtocol.ValidateOutputReports(reports.Select(report => (report.ReportID, report.Length)));

        var stream = device.Open();
        try
        {
            stream.WriteTimeout = 2000;
            stream.ReadTimeout = 1000;
            return new SfrStation(stream, log);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends one beep/blink command and waits 300 ms before allowing further commands.
    /// A successful write is not a station-level acknowledgement.
    /// </summary>
    public void Beep()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var report = SfrProtocol.CreateBeepReport();
            log?.Invoke($"TX попытка ({report.Length} байт): {Convert.ToHexString(report)}");
            stream.Write(report, 0, report.Length);
            // Match SportOrg's post-signal delay. Keep the lock so reads also wait.
            Thread.Sleep(300);
            log?.Invoke("TX завершён: запись HID-отчёта выполнена без ошибки. Ответ станции не проверялся.");
        }
    }

    /// <summary>
    /// Reads cells 01, 03 and 01 again. Returns null for the card-unavailable response 2A 06.
    /// Keep one card stationary throughout the operation; cell 01 is not a unique identity.
    /// After a timeout or invalid exchange, reopen the connection before reading again.
    /// </summary>
    public SfrCardNumber? ReadCardNumber()
    {
        lock (sync)
        {
            EnsureExchangeAvailable();
            try
            {
                var snapshot = ReadSnapshot();
                return snapshot is null ? null : new SfrCardNumber(
                    SfrProtocol.DecodeCardNumber(snapshot.Value.NumberCell), Convert.ToHexString(snapshot.Value.Marker));
            }
            catch
            {
                // There is no transaction ID; a late response must not satisfy the next read.
                exchangeFaulted = true;
                throw;
            }
        }
    }

    /// <summary>
    /// Changes only cell 03, zeroing its last two bytes, and verifies the entire cell by reading back.
    /// The caller does not need to know the current number.
    /// </summary>
    public SfrCardNumber WriteCardNumber(int newNumber) => WriteCardNumberCore(null, newNumber);

    /// <summary>
    /// Changes only cell 03 after checking the current number and verifies the entire cell by reading back.
    /// Keep the same single card on the station. No retry or rollback is performed after a write attempt.
    /// </summary>
    public SfrCardNumber WriteCardNumber(int expectedCurrentNumber, int newNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedCurrentNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedCurrentNumber, 7999999);
        return WriteCardNumberCore(expectedCurrentNumber, newNumber);
    }

    internal SfrCardNumber WriteCardNumberCancellable(int expectedCurrentNumber, int newNumber,
        CancellationToken cancellationToken) => WriteCardNumberCore(expectedCurrentNumber, newNumber, cancellationToken);

    private SfrCardNumber WriteCardNumberCore(int? expectedCurrentNumber, int newNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(newNumber, MaximumWritableCardNumber);

        lock (sync)
        {
            EnsureExchangeAvailable();
            cancellationToken.ThrowIfCancellationRequested();
            var writeAttempted = false;
            try
            {
                var snapshot = ReadSnapshot()
                    ?? throw new IOException("Чип не обнаружен или недоступен. Запись не отправлена.");
                cancellationToken.ThrowIfCancellationRequested();
                int currentNumber = SfrProtocol.DecodeCardNumber(snapshot.NumberCell);
                if (expectedCurrentNumber.HasValue && currentNumber != expectedCurrentNumber.Value)
                {
                    throw new InvalidOperationException(
                        $"Ожидался номер {expectedCurrentNumber}, прочитан {currentNumber}. Запись не отправлена.");
                }
                byte[] desiredCell = SfrProtocol.EncodeCardNumber(newNumber);
                if (snapshot.NumberCell.AsSpan().SequenceEqual(desiredCell))
                {
                    log?.Invoke($"Номер уже равен {newNumber}. Запись не требуется.");
                    return new SfrCardNumber(currentNumber, Convert.ToHexString(snapshot.Marker));
                }

                var request = SfrProtocol.CreateNumberWriteReport(desiredCell);
                log?.Invoke($"Запись номера: {currentNumber} → {newNumber}, только ячейка 03.");
                log?.Invoke($"TX попытка ({request.Length} байт): {Convert.ToHexString(request)}");
                cancellationToken.ThrowIfCancellationRequested();
                writeAttempted = true;
                stream.Write(request, 0, request.Length);
                ReadWriteResponse();

                var verified = ReadSnapshot()
                    ?? throw new IOException("Чип недоступен после записи.");
                if (!verified.Marker.AsSpan().SequenceEqual(snapshot.Marker) ||
                    !verified.NumberCell.AsSpan().SequenceEqual(desiredCell))
                {
                    throw new InvalidDataException("Проверка записи не пройдена: данные чипа не совпадают с ожидаемыми.");
                }
                log?.Invoke($"Запись подтверждена повторным чтением: номер {newNumber}.");
                return new SfrCardNumber(newNumber, Convert.ToHexString(verified.Marker));
            }
            catch (OperationCanceledException) when (!writeAttempted)
            {
                // The preceding read finished normally; cancellation did not touch card memory.
                throw;
            }
            catch (Exception error)
            {
                exchangeFaulted = true;
                if (writeAttempted)
                {
                    throw new SfrWriteUnverifiedException(newNumber, error);
                }
                throw;
            }
        }
    }

    private void EnsureExchangeAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (exchangeFaulted)
        {
            throw new InvalidOperationException(
                "Предыдущий обмен завершился ошибкой. Закройте соединение и откройте станцию заново.");
        }
    }

    private (byte[] Marker, byte[] NumberCell)? ReadSnapshot()
    {
        var marker = ReadCell(1);
        if (marker is null)
        {
            return null;
        }
        var numberCell = ReadCell(3)
            ?? throw new IOException("Чип убран во время чтения номера.");
        var finalMarker = ReadCell(1);
        if (finalMarker is null || !marker.AsSpan().SequenceEqual(finalMarker))
        {
            throw new IOException("Чип убран или его данные изменились во время чтения. Повторите чтение.");
        }
        return (marker, numberCell);
    }

    private void ReadWriteResponse()
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var payload = ReceivePayload(started);
            if (payload[0] == 3)
            {
                log?.Invoke("Пропущен ответ на предыдущую команду сигнала.");
                continue;
            }
            if (payload.Length != 2 || payload[0] != 0x2A || payload[1] != 0x55)
            {
                throw new InvalidDataException($"Неожиданный ответ записи SFR: {Convert.ToHexString(payload)}");
            }
            // An acknowledgement alone does not prove the requested data reached the card.
            return;
        }
    }

    private byte[]? ReadCell(byte position)
    {
        var request = SfrProtocol.CreateReadReport(position);
        log?.Invoke($"TX попытка ({request.Length} байт): {Convert.ToHexString(request)}");
        stream.Write(request, 0, request.Length);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var payload = ReceivePayload(started);
            if (payload.AsSpan().SequenceEqual(new byte[] { 0x2A, 0x06 }))
            {
                log?.Invoke("Станция вернула 2A 06: чип не обнаружен или недоступен для чтения.");
                return null;
            }
            if (payload[0] == 0x03)
            {
                log?.Invoke("Пропущен ответ на предыдущую команду сигнала.");
                continue;
            }
            if (payload[0] != 0x01)
            {
                throw new InvalidDataException($"Неизвестный ответ SFR: {Convert.ToHexString(payload)}");
            }
            if (payload.Length != 6)
            {
                throw new InvalidDataException("Некорректная длина ответа чтения ячейки SFR.");
            }
            if (payload[1] != position)
            {
                log?.Invoke($"Пропущен ответ другой ячейки: {payload[1]:X2}, ожидалась {position:X2}.");
                continue;
            }
            return payload[2..6];
        }
    }

    private byte[] ReceivePayload(long started)
    {
        var remaining = 1000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (remaining <= 0)
        {
            throw new TimeoutException("Станция не ответила на команду за 1000 мс.");
        }
        stream.ReadTimeout = remaining;
        var report = new byte[SfrProtocol.ReportLength];
        var count = stream.Read(report, 0, report.Length);
        if (count == 0)
        {
            throw new IOException("Соединение со станцией закрыто во время чтения.");
        }
        log?.Invoke($"RX ({count} байт): {Convert.ToHexString(report.AsSpan(0, count))}");
        return SfrProtocol.ParseFrame(report.AsSpan(0, count));
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stream.Dispose();
        }
    }

    private static SfrDeviceInfo Inspect(HidDevice device)
    {
        var errors = new List<string>();
        string name = device.DevicePath;
        int? inputLength = null;
        int? outputLength = null;
        try { name = device.GetProductName(); }
        catch (Exception error) { errors.Add($"Название: {error.Message}"); }
        try { inputLength = device.GetMaxInputReportLength(); }
        catch (Exception error) { errors.Add($"Input: {error.Message}"); }
        try { outputLength = device.GetMaxOutputReportLength(); }
        catch (Exception error) { errors.Add($"Output: {error.Message}"); }

        // An unavailable property must not hide the device from diagnostics.
        return new SfrDeviceInfo(device.DevicePath, device.VendorID, device.ProductID,
            name, inputLength, outputLength, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("На этом этапе SfrConnect поддерживает только Windows.");
        }
    }
}
