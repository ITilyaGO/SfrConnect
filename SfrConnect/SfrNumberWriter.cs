namespace SfrConnect;

public enum SfrAssignmentStage { WaitingForRemoval, WaitingForCard, Writing, Clearing }

/// <summary>A verified number. SignalSent confirms USB transmission, not audible feedback.</summary>
public sealed record SfrNumberAssignmentResult(SfrCardNumber Card, bool SignalSent, string? SignalError)
{
    public bool CardCleared { get; init; }
}

/// <summary>
/// Waits for a card, writes and verifies one number, then sends the success signal.
/// Reuse one writer per connection. Do not poll or dispose the station while a command is active.
/// Cancel and await the command before closing the connection.
/// </summary>
public sealed class SfrNumberWriter
{
    private readonly SfrStation station;
    private int running;

    public SfrNumberWriter(SfrStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        this.station = station;
    }

    /// <summary>
    /// Waits indefinitely unless canceled. Cancellation is honored up to the write attempt;
    /// once writing begins, verification and the final result take precedence over cancellation.
    /// Set waitForRemoval when a previous card must first be removed (two absent samples).
    /// </summary>
    public Task<SfrNumberAssignmentResult> AssignAsync(int number,
        CancellationToken cancellationToken = default,
        IProgress<SfrAssignmentStage>? progress = null, bool waitForRemoval = false, bool clearCard = false)
        => RunAsync(number, cancellationToken, progress, waitForRemoval, clearCard);

    /// <summary>Clears punches and converts service cards. A service card without a valid number gets zero.</summary>
    public Task<SfrNumberAssignmentResult> ClearAsync(CancellationToken cancellationToken = default,
        IProgress<SfrAssignmentStage>? progress = null, bool waitForRemoval = false)
        => RunAsync(null, cancellationToken, progress, waitForRemoval, true);

    private async Task<SfrNumberAssignmentResult> RunAsync(int? number, CancellationToken cancellationToken,
        IProgress<SfrAssignmentStage>? progress, bool waitForRemoval, bool clearCard)
    {
        if (number.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(number.Value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(number.Value, SfrStation.MaximumWritableCardNumber);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Команда записи уже выполняется.");
        try
        {
            using var total = station.MeasureStage(number.HasValue
                ? $"ВСЯ КОМАНДА: записать {number}{(clearCard ? " с очисткой" : "")}" : "ВСЯ КОМАНДА: очистить чип");
            var result = await Task.Run(() => AssignCore(number, cancellationToken, progress, waitForRemoval, clearCard))
                .ConfigureAwait(false);
            total.Finish();
            return result;
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private SfrNumberAssignmentResult AssignCore(int? number, CancellationToken cancellationToken,
        IProgress<SfrAssignmentStage>? progress, bool waitForRemoval, bool clearCard)
    {
        using var timing = station.MeasureStage(waitForRemoval ? "Ожидание снятия предыдущего чипа" : "Ожидание и обнаружение чипа");
        progress?.Report(waitForRemoval ? SfrAssignmentStage.WaitingForRemoval : SfrAssignmentStage.WaitingForCard);
        var absentSamples = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var card = station.ReadCardInfo();
                cancellationToken.ThrowIfCancellationRequested();
                if (waitForRemoval)
                {
                    absentSamples = card is null ? absentSamples + 1 : 0;
                    if (absentSamples >= 2)
                    {
                        waitForRemoval = false;
                        timing.Next("Ожидание и обнаружение чипа");
                        progress?.Report(SfrAssignmentStage.WaitingForCard);
                    }
                }
                else if (card is not null)
                {
                    if (card.Kind == SfrCardKind.Unknown)
                        throw new SfrProtectedCardException(card.Kind, Convert.FromHexString(card.HeaderHex));
                    bool mustClear = clearCard || card.Kind == SfrCardKind.Service;
                    timing.Finish();
                    if (!mustClear) timing.Next("Запись номера и проверка");
                    progress?.Report(mustClear ? SfrAssignmentStage.Clearing : SfrAssignmentStage.Writing);
                    var verified = mustClear
                        ? station.ClearCardCore(number, card.Number, cancellationToken)
                        : station.WriteCardNumberCancellable(card.Number!.Value, number!.Value, cancellationToken);
                    try
                    {
                        timing.Next("Сигнал и пауза станции 300 мс");
                        station.Beep();
                        timing.Finish();
                        return new SfrNumberAssignmentResult(verified, true, null) { CardCleared = mustClear };
                    }
                    catch (Exception error)
                    {
                        // A signal failure cannot turn a verified write into a failed write.
                        return new SfrNumberAssignmentResult(verified, false, error.Message) { CardCleared = mustClear };
                    }
                }
            }
            catch (SfrCardNotReadyException)
            {
                // Only completed reads before any write attempt are safe to repeat.
                absentSamples = 0;
                timing.Finish(success: false);
                timing.Next(waitForRemoval ? "Ожидание снятия предыдущего чипа" : "Ожидание и обнаружение чипа");
                progress?.Report(waitForRemoval ? SfrAssignmentStage.WaitingForRemoval : SfrAssignmentStage.WaitingForCard);
            }
            if (cancellationToken.WaitHandle.WaitOne(250)) cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
