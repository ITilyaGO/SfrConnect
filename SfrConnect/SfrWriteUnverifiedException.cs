namespace SfrConnect;

/// <summary>The write was attempted, but its final state could not be verified.</summary>
public sealed class SfrWriteUnverifiedException : IOException
{
    public int RequestedNumber { get; }
    public byte LastAttemptedCell { get; }

    internal SfrWriteUnverifiedException(int requestedNumber, Exception innerException, byte lastAttemptedCell = 3)
        : base($"Запись номера {requestedNumber} не подтверждена. Номер или метка времени в чипе могли уже измениться. " +
               "Откройте соединение заново и прочитайте чип перед следующей записью.", innerException)
    {
        RequestedNumber = requestedNumber;
        LastAttemptedCell = lastAttemptedCell;
    }
}
