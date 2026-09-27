namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class EnrollmentQrCodeCameraScannerService
{
    private IEnrollmentQrCodeCameraScanner? _platformScanner;

    public bool IsAvailable => _platformScanner?.IsAvailable == true;



    public void SetPlatformScanner(IEnrollmentQrCodeCameraScanner? platformScanner) =>
        _platformScanner = platformScanner;



    public Task<string?> ScanEnrollmentCodeAsync(
        string? title = null,
        string? description = null,
        CancellationToken cancellationToken = default) =>
        _platformScanner?.ScanEnrollmentCodeAsync(title, description, cancellationToken) ?? Task.FromResult<string?>(null);
}
