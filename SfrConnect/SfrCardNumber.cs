namespace SfrConnect;

/// <summary>
/// The stored number and the four bytes from cell 01. The marker is not a unique card ID.
/// </summary>
public sealed record SfrCardNumber(int Number, string MarkerHex);
