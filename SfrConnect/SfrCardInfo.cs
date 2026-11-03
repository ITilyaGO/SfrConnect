namespace SfrConnect;

public enum SfrCardKind { Participant, Service, Unknown }

/// <summary>Header classification; the marker is a formatting time, not a unique identity.</summary>
public sealed record SfrCardInfo(SfrCardKind Kind, int? Number, string MarkerHex, string HeaderHex);

public sealed class SfrProtectedCardException : IOException
{
    public SfrCardKind Kind { get; }
    public string HeaderHex { get; }

    internal SfrProtectedCardException(SfrCardKind kind, byte[] header)
        : base(kind == SfrCardKind.Service
            ? "Обнаружен служебный чип: требуется перевод в обычный режим."
            : $"Неизвестный формат чипа (заголовок {Convert.ToHexString(header)}). Для приведения с очисткой включите FormatUnknownCards при открытии станции.")
    {
        Kind = kind;
        HeaderHex = Convert.ToHexString(header);
    }
}

/// <summary>A multi-cell operation may have partially changed the card. Never retry automatically.</summary>
public sealed class SfrCardUpdateUnverifiedException : IOException
{
    public int? RequestedNumber { get; }
    public byte LastAttemptedCell { get; }

    internal SfrCardUpdateUnverifiedException(int? number, byte cell, Exception error)
        : base($"Очистка не подтверждена (ячейка {cell:X2}). Данные чипа могли измениться частично. " +
               "Подключитесь заново и проверьте чип перед следующей операцией.", error)
    {
        RequestedNumber = number;
        LastAttemptedCell = cell;
    }
}
