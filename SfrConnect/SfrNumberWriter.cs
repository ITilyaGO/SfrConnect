using System.Diagnostics.CodeAnalysis;

namespace SfrConnect;

public enum SfrAssignmentStage { WaitingForRemoval, WaitingForCard, Writing, Clearing }

public enum SfrAssignmentFailure { WriteUnverified, ClearUnverified, PreparationFailed }

/// <summary>Check Success before using Card. SignalSent confirms transmission, not audible feedback.</summary>
public sealed record SfrNumberAssignmentResult(SfrCardNumber? Card, bool SignalSent, string? SignalError)
{
    [MemberNotNullWhen(true, nameof(Card))]
    public bool Success => Card is not null;
    public bool CardCleared { get; init; }
    /// <summary>Last cell in the final verification pass on success; null when no clearing was confirmed. In shortened mode the empty tail is checked only before writing.</summary>
    public byte? ClearedThroughCell { get; init; }
    public SfrAssignmentFailure? Failure { get; init; }
    public string? Error { get; init; }
    public int? RequestedNumber { get; init; }
    public byte? LastAttemptedCell { get; init; }
    public bool CardMayHaveChanged => Failure is SfrAssignmentFailure.WriteUnverified or SfrAssignmentFailure.ClearUnverified;
    public bool RequiresReconnect { get; init; }

    internal static SfrNumberAssignmentResult PreparationFailed(int? number, string message)
        => new(null, false, null)
        {
            Failure = SfrAssignmentFailure.PreparationFailed, Error = message, RequestedNumber = number
        };

    internal static SfrNumberAssignmentResult Unverified(int? number, string message, byte? cell = 3, bool clearing = false, bool reconnect = false)
        => new(null, false, null)
        {
            Failure = clearing ? SfrAssignmentFailure.ClearUnverified : SfrAssignmentFailure.WriteUnverified,
            Error = message, RequestedNumber = number, LastAttemptedCell = cell, RequiresReconnect = reconnect
        };
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

    /// <summary>Clears punches and normalizes service/allowed unknown cards. A normalized card without a valid number gets zero.</summary>
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
            SfrNumberAssignmentResult result;
            try
            {
                result = await Task.Run(() => AssignCore(number, cancellationToken, progress, waitForRemoval, clearCard))
                    .ConfigureAwait(false);
            }
            catch (SfrWriteUnverifiedException error)
            {
                result = SfrNumberAssignmentResult.Unverified(number, error.InnerException?.Message ?? error.Message, reconnect: true);
            }
            catch (SfrCardUpdateUnverifiedException error)
            {
                result = SfrNumberAssignmentResult.Unverified(number, error.InnerException?.Message ?? error.Message,
                    error.LastAttemptedCell, clearing: true, reconnect: true);
            }
            total.Finish(result.Success);
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
                    if (card.Kind == SfrCardKind.Unknown && !station.FormatUnknownCards)
                        throw new SfrProtectedCardException(card.Kind, Convert.FromHexString(card.HeaderHex));
                    bool mustClear = clearCard || card.Kind != SfrCardKind.Participant;
                    timing.Finish();
                    if (!mustClear) timing.Next("Запись номера и проверка");
                    progress?.Report(mustClear ? SfrAssignmentStage.Clearing : SfrAssignmentStage.Writing);
                    var write = mustClear
                        ? station.TryClearCardCore(number, card.Number, cancellationToken)
                        : station.WriteCardNumberCancellable(card.Number!.Value, number!.Value, cancellationToken);
                    if (!write.Success) return write;
                    var verified = write.Card;
                    try
                    {
                        timing.Next("Сигнал и пауза станции 300 мс");
                        station.Beep();
                        timing.Finish();
                        return new SfrNumberAssignmentResult(verified, true, null)
                        { CardCleared = mustClear, ClearedThroughCell = write.ClearedThroughCell };
                    }
                    catch (Exception error)
                    {
                        // A signal failure cannot turn a verified write into a failed write.
                        return new SfrNumberAssignmentResult(verified, false, error.Message)
                        { CardCleared = mustClear, ClearedThroughCell = write.ClearedThroughCell };
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
