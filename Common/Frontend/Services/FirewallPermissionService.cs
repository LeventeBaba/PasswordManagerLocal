namespace PasswordManagerLocal.Common.Frontend.Services;

public static class FirewallPermissionService
{
    private static IFirewallPermissionManager? _platformFirewallPermissionManager;

    public static void SetPlatformFirewallPermissionManager(IFirewallPermissionManager? platformFirewallPermissionManager)
    {
        _platformFirewallPermissionManager = platformFirewallPermissionManager;
    }



    public static Task<FirewallPermissionCheckResult> CheckAsync(CancellationToken ct = default) =>
        _platformFirewallPermissionManager?.CheckAsync(ct) ?? Task.FromResult(FirewallPermissionCheckResult.Unsupported());



    public static Task<FirewallPermissionCheckResult> RequestPermissionAsync(CancellationToken ct = default) =>
        _platformFirewallPermissionManager?.RequestPermissionAsync(ct) ?? Task.FromResult(FirewallPermissionCheckResult.Unsupported());
}
