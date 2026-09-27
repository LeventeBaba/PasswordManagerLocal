namespace PasswordManagerLocal.Common.Frontend.Services;

public sealed class FirewallPermissionCheckResult
{
    public bool IsSupported { get; set; }
    public bool IsConfigured { get; set; }
    public bool CanRequestPermission { get; set; }
    public string? Details { get; set; }

    public static FirewallPermissionCheckResult Unsupported() =>
        new()
        {
            IsSupported = false,
            IsConfigured = true,
            CanRequestPermission = false
        };
}
