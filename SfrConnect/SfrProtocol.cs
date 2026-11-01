namespace SfrConnect;

internal static class SfrProtocol
{
    internal const byte ReportId = 0x3F;
    internal const int ReportLength = 64;

    internal static byte[] EncodeCardNumber(int number, ReadOnlySpan<byte> previousCell)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, SfrStation.MaximumWritableCardNumber);
        if (previousCell.Length != 4)
        {
            throw new ArgumentException("Ячейка номера должна содержать четыре байта.", nameof(previousCell));
        }
        // SFR basic writes two base-200 digits. Preserve the unrelated fourth byte.
        return [(byte)(number % 200), (byte)(number / 200), 0, previousCell[3]];
    }

    internal static byte[] CreateNumberWriteReport(ReadOnlySpan<byte> cell)
    {
        if (cell.Length != 4)
        {
            throw new ArgumentException("Ячейка номера должна содержать четыре байта.", nameof(cell));
        }
        var report = new byte[ReportLength];
        report[0] = ReportId;
        report[1] = 9;
        report[2] = 0xFD;
        report[3] = 2;
        report[4] = 3;
        cell.CopyTo(report.AsSpan(5, 4));
        report[9] = Checksum(report.AsSpan(3, 6));
        report[10] = 0xFE;
        return report;
    }

    internal static byte[] CreateReadReport(byte position)
    {
        var report = new byte[ReportLength];
        byte[] command = [ReportId, 0x05, 0xFD, 0x01, position,
            Checksum([0x01, position]), 0xFE];
        command.CopyTo(report, 0);
        return report;
    }

    private static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var value in bytes)
        {
            sum += value;
            sum = (sum & 0xFF) + (sum >> 8);
        }
        return (byte)sum;
    }

    internal static byte[] ParseFrame(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength || report[0] != ReportId)
        {
            throw new InvalidDataException("Некорректная длина или Report ID ответа SFR.");
        }
        int length = report[1];
        if (length < 4 || length > ReportLength - 2 ||
            report[2] != 0xFD || report[length + 1] != 0xFE)
        {
            throw new InvalidDataException("Некорректные границы пакета SFR.");
        }

        // The tested station returns zero in the checksum slot and arbitrary HID padding.
        // Only the payload enclosed by the declared length is meaningful.
        return report.Slice(3, length - 3).ToArray();
    }

    internal static int DecodeCardNumber(ReadOnlySpan<byte> cell)
    {
        if (cell.Length != 4 || cell[0] >= 200 || cell[1] >= 200 || cell[2] >= 200)
        {
            throw new InvalidDataException("Номер в чипе не соответствует формату SFR (основание 200).");
        }
        return cell[0] + cell[1] * 200 + cell[2] * 40000;
    }

    internal static byte[] CreateBeepReport()
    {
        // The first byte is already the HID report ID; do not prepend a zero.
        var report = new byte[ReportLength];
        byte[] command = [ReportId, 0x05, 0xFD, 0x03, 0x01, 0x04, 0xFE];
        command.CopyTo(report, 0);
        return report;
    }

    internal static void ValidateOutputReports(IEnumerable<(byte Id, int Length)> reports)
    {
        if (!reports.Any(report => report.Id == ReportId && report.Length == ReportLength))
        {
            throw new NotSupportedException(
                "Устройство не объявляет выходной HID-отчёт 0x3F длиной 64 байта. " +
                "Этот вариант станции пока не поддерживается; команда не отправлена.");
        }
    }
}
