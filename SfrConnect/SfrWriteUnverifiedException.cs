namespace SfrConnect;

/// <summary>The write was attempted, but its final state could not be verified.</summary>
public sealed class SfrWriteUnverifiedException : IOException
{
    public int RequestedNumber { get; }

    internal SfrWriteUnverifiedException(int requestedNumber, Exception innerException)
        : base($"Запись номера {requestedNumber} не подтверждена. Номер в чипе мог уже измениться. " +
               "Откройте соединение заново и прочитайте чип перед следующей записью.", innerException)
    {
        RequestedNumber = requestedNumber;
    }
}
