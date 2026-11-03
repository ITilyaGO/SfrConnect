namespace SfrConnect;

public enum SfrClearMode
{
    FullMemory,
    ThroughLastNonEmptyCell
}

/// <summary>Settings captured when a station connection is opened.</summary>
public sealed class SfrStationOptions
{
    /// <summary>Wait after the first unavailable preparation or verification reply. Zero disables retries.</summary>
    public int VerificationReturnTimeoutMs { get; init; } = 5000;

    /// <summary>Delay between verification reads while waiting for the card to return.</summary>
    public int VerificationRetryIntervalMs { get; init; } = 200;

    /// <summary>Normalize unknown headers by clearing the supported memory before assignment. Existing data is erased.</summary>
    public bool FormatUnknownCards { get; init; } = true;

    /// <summary>ThroughLastNonEmptyCell scans all memory first, then verifies through the last occupied cell. Service and unknown cards always use FullMemory.</summary>
    public SfrClearMode ClearMode { get; init; } = SfrClearMode.FullMemory;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(VerificationReturnTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(VerificationRetryIntervalMs);
        if (!Enum.IsDefined(ClearMode)) throw new ArgumentOutOfRangeException(nameof(ClearMode));
    }
}
