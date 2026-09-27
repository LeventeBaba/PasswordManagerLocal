namespace PasswordManagerLocal.Common.Frontend.Services;

public interface IEnrollmentQrCodeCameraScanner
{
    bool IsAvailable { get; }

    Task<string?> ScanEnrollmentCodeAsync(
        string? title = null,
        string? description = null,
        CancellationToken cancellationToken = default);
}
