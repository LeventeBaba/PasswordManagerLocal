using PasswordManagerLocal.Common.Frontend.Services;

namespace PasswordManagerLocal.Common.Tests.Fakes;

internal sealed class FakeBackgroundSyncSettingsClient : IBackgroundSyncSettingsClient
{
    public BackgroundSyncClientState State { get; set; } = Available(false);
    public BackgroundSyncChangeResult? SetResult { get; set; }
    public Exception? GetFailure { get; set; }
    public Exception? SetFailure { get; set; }
    public int GetCount { get; private set; }
    public int SetCount { get; private set; }
    public bool? LastRequestedEnabled { get; private set; }
    public TaskCompletionSource? GetEntered { get; set; }
    public Task? GetRelease { get; set; }
    public TaskCompletionSource? SetEntered { get; set; }
    public Task? SetRelease { get; set; }

    public async Task<BackgroundSyncClientState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetCount++;
        GetEntered?.TrySetResult();
        if (GetRelease is not null)
            await GetRelease.WaitAsync(cancellationToken);
        if (GetFailure is not null)
            throw GetFailure;
        return State;
    }

    public async Task<BackgroundSyncChangeResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCount++;
        LastRequestedEnabled = isEnabled;
        SetEntered?.TrySetResult();
        if (SetRelease is not null)
            await SetRelease.WaitAsync(cancellationToken);
        if (SetFailure is not null)
            throw SetFailure;

        var result = SetResult ?? new BackgroundSyncChangeResult(
            Available(isEnabled),
            WasOutcomeUncertain: false);
        State = result.State;
        return result;
    }

    public static BackgroundSyncClientState Available(bool enabled) => new(
        enabled,
        IsAvailable: true,
        IsDegraded: false,
        IsTransitionInProgress: false,
        BackgroundSyncClientFailureKind.None,
        SafeMessage: null);
}
