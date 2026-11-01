namespace SfrConnect;

/// <summary>
/// Valid replies indicate the card disappeared or changed during a read.
/// No write was attempted by the failed operation; the connection remains usable.
/// Failures after a write attempt are reported as SfrWriteUnverifiedException instead.
/// </summary>
public sealed class SfrCardNotReadyException : IOException
{
    internal SfrCardNotReadyException(string message) : base(message) { }
}
