namespace PasswordManagerLocal.Common.Contracts.BackgroundSync;

public sealed record BackgroundSyncChangeResult(
    BackgroundSyncClientState State,
    bool WasOutcomeUncertain);
