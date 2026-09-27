namespace PasswordManagerLocal.Common.Frontend.Services;

public interface IFirewallPermissionManager
{
    Task<FirewallPermissionCheckResult> CheckAsync(CancellationToken ct = default);
    Task<FirewallPermissionCheckResult> RequestPermissionAsync(CancellationToken ct = default);
}
