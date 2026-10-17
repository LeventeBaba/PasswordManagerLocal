namespace PasswordManagerLocal.Common.Frontend.Services;

/// <summary>UI-session operations used by the refresh coordinator.</summary>
public interface IFrontendRefreshTarget
{
    bool IsActive { get; }
    Guid CurrentAuthenticationToken { get; }
    Guid? CurrentUserId { get; }
    IReadOnlyCollection<FrontendRefreshPage> GetVisiblePages();
    bool CanRefreshPage(FrontendRefreshPage page);

    Task<bool> RefreshPageAsync(
        FrontendRefreshPage page,
        PasswordManagerLocal.Common.Contracts.Notifications.FrontendChangeScope scopes,
        Func<bool> isCurrent,
        CancellationToken cancellationToken);

    Task<bool> RevalidateCurrentSessionAsync(
        Func<bool> isCurrent,
        CancellationToken cancellationToken);
}
