namespace SfrConnect;

public enum SfrAssignmentStage { WaitingForRemoval, WaitingForCard, Writing }

/// <summary>A verified number. SignalSent confirms USB transmission, not audible feedback.</summary>
public sealed record SfrNumberAssignmentResult(SfrCardNumber Card, bool SignalSent, string? SignalError);

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
    public async Task<SfrNumberAssignmentResult> AssignAsync(int number,
        CancellationToken cancellationToken = default,
        IProgress<SfrAssignmentStage>? progress = null, bool waitForRemoval = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, SfrStation.MaximumWritableCardNumber);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("Команда записи уже выполняется.");
        try
        {
            return await Task.Run(() => AssignCore(number, cancellationToken, progress, waitForRemoval))
                .ConfigureAwait(false);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    private SfrNumberAssignmentResult AssignCore(int number, CancellationToken cancellationToken,
        IProgress<SfrAssignmentStage>? progress, bool waitForRemoval)
    {
        progress?.Report(waitForRemoval ? SfrAssignmentStage.WaitingForRemoval : SfrAssignmentStage.WaitingForCard);
        var absentSamples = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var card = station.ReadCardNumber();
                cancellationToken.ThrowIfCancellationRequested();
                if (waitForRemoval)
                {
                    absentSamples = card is null ? absentSamples + 1 : 0;
                    if (absentSamples >= 2)
                    {
                        waitForRemoval = false;
                        progress?.Report(SfrAssignmentStage.WaitingForCard);
                    }
                }
                else if (card is not null)
                {
                    progress?.Report(SfrAssignmentStage.Writing);
                    var verified = station.WriteCardNumberCancellable(card.Number, number, cancellationToken);
                    try
                    {
                        station.Beep();
                        return new SfrNumberAssignmentResult(verified, true, null);
                    }
                    catch (Exception error)
                    {
                        // A signal failure cannot turn a verified write into a failed write.
                        return new SfrNumberAssignmentResult(verified, false, error.Message);
                    }
                }
            }
            catch (SfrCardNotReadyException)
            {
                // Only completed reads before any write attempt are safe to repeat.
                absentSamples = 0;
                progress?.Report(waitForRemoval ? SfrAssignmentStage.WaitingForRemoval : SfrAssignmentStage.WaitingForCard);
            }
            if (cancellationToken.WaitHandle.WaitOne(250)) cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
