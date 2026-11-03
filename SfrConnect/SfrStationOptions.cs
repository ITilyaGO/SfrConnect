namespace SfrConnect;

/// <summary>Settings captured when a station connection is opened.</summary>
public sealed class SfrStationOptions
{
    /// <summary>Wait after the first unavailable verification reply. Zero disables retries.</summary>
    public int VerificationReturnTimeoutMs { get; init; } = 5000;

    /// <summary>Delay between verification reads while waiting for the card to return.</summary>
    public int VerificationRetryIntervalMs { get; init; } = 200;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(VerificationReturnTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(VerificationRetryIntervalMs);
    }
}
