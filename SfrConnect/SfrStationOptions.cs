namespace SfrConnect;

/// <summary>Settings captured when a station connection is opened.</summary>
public sealed class SfrStationOptions
{
    /// <summary>Wait after the first unavailable preparation or verification reply. Zero disables retries.</summary>
    public int VerificationReturnTimeoutMs { get; init; } = 5000;

    /// <summary>Delay between verification reads while waiting for the card to return.</summary>
    public int VerificationRetryIntervalMs { get; init; } = 200;

    /// <summary>Normalize unknown headers by clearing the supported memory before assignment. Existing data is erased.</summary>
    public bool FormatUnknownCards { get; init; } = true;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(VerificationReturnTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(VerificationRetryIntervalMs);
    }
}
