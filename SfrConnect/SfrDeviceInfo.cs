namespace SfrConnect;

public sealed record SfrDeviceInfo(
    string DevicePath,
    int VendorId,
    int ProductId,
    string ProductName,
    int? InputReportLength,
    int? OutputReportLength,
    string? InspectionError)
{
    public bool IsSfr => VendorId == SfrStation.VendorId && ProductId == SfrStation.ProductId;
}
