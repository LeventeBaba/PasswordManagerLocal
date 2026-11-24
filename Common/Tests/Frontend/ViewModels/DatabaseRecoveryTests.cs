using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Tests.Fakes;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class DatabaseRecoveryTests
{
    [TestMethod]
    [DataRow(AppLanguage.English, 0, "Database version not supported")]
    [DataRow(AppLanguage.English, 1, "Database configuration missing or damaged")]
    [DataRow(AppLanguage.English, 2, "Database encryption key unavailable")]
    [DataRow(AppLanguage.English, 3, "Database cannot be read")]
    [DataRow(AppLanguage.Hungarian, 0, "Nem támogatott adatbázis-verzió")]
    [DataRow(AppLanguage.Hungarian, 1, "Hiányzó vagy sérült adatbázis-konfiguráció")]
    [DataRow(AppLanguage.Hungarian, 2, "Az adatbázis titkosítási kulcsa nem érhető el")]
    [DataRow(AppLanguage.Hungarian, 3, "Az adatbázis nem olvasható")]
    public async Task EachCauseHasItsOwnNoticeAndRequiresTwoExplicitResetActions(
        AppLanguage language, int failureCase, string title)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            new ApplicationPreferences { Language = language, Theme = AppThemeId.Dark },
            resourceLoader: new ShippedLocalizationLoader());
        var (failure, kind) = CreateFailure(failureCase);
        await using var runtime = new FailedRuntime(failure, kind);
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);

        await shell.InitializeAsync();

        Assert.IsTrue(shell.IsDatabaseRecoveryDialogOpen);
        Assert.IsFalse(shell.IsApplicationInteractionEnabled);
        Assert.AreEqual(title, shell.DatabaseRecoveryTitle);
        Assert.AreEqual(0, runtime.ResetCalls);
        var originalMessage = shell.DatabaseRecoveryMessage;

        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.AreEqual(0, runtime.ResetCalls, "The first action must only show permanent-erasure confirmation.");
        await ExecuteAndWaitAsync(shell.DatabaseRecoverySecondaryCommand);
        Assert.AreEqual(title, shell.DatabaseRecoveryTitle);
        Assert.AreEqual(originalMessage, shell.DatabaseRecoveryMessage);

        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.AreEqual(0, runtime.ResetCalls, "Returning from cancellation must require a new confirmation action.");
        shell.IsDatabaseResetAcknowledged = true;
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);

        Assert.AreEqual(1, runtime.ResetCalls);
        Assert.IsFalse(shell.IsDatabaseRecoveryDialogOpen);
        Assert.IsTrue(shell.IsApplicationInteractionEnabled);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
    }

    [TestMethod]
    public async Task TemporarilyLockedKeyDoesNotOfferDestructiveRecovery()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        await using var runtime = new FailedRuntime(
            new KeyProtectorUnavailableException(KeyProtectorUnavailableReason.DeviceLocked),
            BackendRuntimeFailureKind.PlatformKeyUnavailable);
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);

        await shell.InitializeAsync();

        Assert.IsFalse(shell.IsDatabaseRecoveryDialogOpen);
        Assert.AreEqual(0, runtime.ResetCalls);
    }

    [TestMethod]
    public async Task GlobalConfirmationAndMissingAcknowledgementCannotEraseStorage()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (failure, kind) = CreateFailure(3);
        await using var runtime = new FailedRuntime(failure, kind);
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        await shell.ConfirmOpenDialogAsync();
        Assert.IsFalse(shell.IsDatabaseResetAcknowledgementVisible);
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.IsTrue(shell.IsDatabaseResetAcknowledgementVisible);
        Assert.IsFalse(shell.IsDatabaseResetAcknowledged);
        Assert.IsFalse(shell.CanConfirmDatabaseReset);
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.AreEqual(0, runtime.ResetCalls);
        shell.IsDatabaseResetAcknowledged = true;
        await shell.ConfirmOpenDialogAsync();
        Assert.AreEqual(0, runtime.ResetCalls, "Global Enter cannot approve an acknowledged reset either.");
        await shell.TryNavigateBackAsync();
        Assert.IsFalse(shell.IsDatabaseResetAcknowledged);
        Assert.IsFalse(shell.IsDatabaseResetAcknowledgementVisible);
    }

    [TestMethod]
    [DataRow(AppLanguage.English)]
    [DataRow(AppLanguage.Hungarian)]
    public async Task WarningExplainsInstallationWideEffectsAndManualEnrollment(AppLanguage language)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync(
            new ApplicationPreferences { Language = language, Theme = AppThemeId.Dark }, resourceLoader: new ShippedLocalizationLoader());
        var (failure, kind) = CreateFailure(1);
        await using var runtime = new FailedRuntime(failure, kind);
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        Assert.IsTrue(shell.CanRetryDatabaseStartup);
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        var message = shell.DatabaseRecoveryMessage;
        StringAssert.Contains(message, language == AppLanguage.English ? "ALL accounts" : "ÖSSZES helyi fiókját");
        StringAssert.Contains(message, language == AppLanguage.English ? "permanently unrecoverable" : "helyreállíthatatlanná");
        StringAssert.Contains(message, "+ (");
        Assert.IsFalse(shell.CanRetryDatabaseStartup);
        Assert.AreEqual(0, runtime.ResetCalls);
    }

    [TestMethod]
    public async Task RetryPreservesTheNoticeAndNeverCallsResetOrAccountEndpoints()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (failure, kind) = CreateFailure(2);
        await using var runtime = new FailedRuntime(failure, kind);
        var endpoints = DispatchProxy.Create<IEndpoints, RejectAllEndpoints>();
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        var notice = shell.DatabaseRecoveryMessage;
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryRetryCommand);
        Assert.AreEqual(2, runtime.ConnectCalls);
        Assert.AreEqual(0, runtime.ResetCalls);
        Assert.AreEqual(0, ((RejectAllEndpoints)(object)endpoints).Calls);
        Assert.IsTrue(shell.IsDatabaseRecoveryDialogOpen);
        Assert.AreEqual(notice, shell.DatabaseRecoveryMessage);
        Assert.IsFalse(shell.IsDatabaseRecoveryBusy);
        Assert.IsFalse(shell.IsDatabaseResetAcknowledged);
    }

    [TestMethod]
    public async Task ResetBusyStateRejectsOverlappingActionsAndFailureDoesNotDisplaySuccess()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (failure, kind) = CreateFailure(3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new FailedRuntime(failure, kind)
        {
            ResetRelease = release.Task,
            ResetFailure = new IOException("Sensitive internal path")
        };
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        shell.IsDatabaseResetAcknowledged = true;
        var reset = ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.IsTrue(shell.IsDatabaseResetInProgress);
        Assert.IsFalse(shell.IsDatabaseRecoveryPrimaryButtonVisible);
        Assert.IsFalse(shell.CanRetryDatabaseStartup);
        await shell.TryNavigateBackAsync();
        await ExecuteAndWaitAsync(shell.DatabaseRecoverySecondaryCommand);
        // Invoke the underlying action too: command scheduling cannot be the safety boundary.
        await (Task)typeof(MainViewModel).GetMethod("HandleDatabaseRecoveryPrimaryActionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null)!;
        Assert.AreEqual(1, runtime.ResetCalls);
        release.SetResult();
        await reset;
        Assert.IsTrue(shell.IsDatabaseRecoveryDialogOpen);
        Assert.IsFalse(shell.IsDatabaseResetInProgress);
        Assert.IsFalse(shell.IsDatabaseResetAcknowledged);
        Assert.AreEqual(preferences.GetString("DatabaseVersion_ResetFailed_Message"), shell.DatabaseRecoveryMessage);
        Assert.IsFalse(shell.DatabaseRecoveryMessage.Contains("Sensitive internal path", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(BackendRuntimeFailureKind.StorageUnavailable)]
    [DataRow(BackendRuntimeFailureKind.StartupFailure)]
    public async Task OrdinaryFailureOffersRetryWithoutDestructiveRecovery(BackendRuntimeFailureKind kind)
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        await using var runtime = new FailedRuntime(new IOException("private details"), kind);
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        Assert.IsFalse(shell.IsDatabaseRecoveryDialogOpen);
        Assert.IsTrue(shell.CanRetryStartup);
        await ExecuteAndWaitAsync(shell.RetryStartupCommand);
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.AreEqual(0, runtime.ResetCalls);
        Assert.AreEqual(2, runtime.ConnectCalls);
        Assert.AreEqual(preferences.GetString(kind == BackendRuntimeFailureKind.StorageUnavailable
            ? "DatabaseRecovery_StorageUnavailable" : "Shell_BackendStartupFailed"), shell.StatusMessage);
    }

    [TestMethod]
    public async Task SuccessfulNonDestructiveRetryReturnsToLoginWithoutResetOrEnrollment()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var endpoints = DispatchProxy.Create<IEndpoints, MainViewModelStartupTests.StartupEndpoints>();
        var (failure, kind) = CreateFailure(3);
        await using var runtime = new FailedRuntime(failure, kind);
        using var shell = new MainViewModel(endpoints, runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        runtime.RecoverOnConnect = true;
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryRetryCommand);
        Assert.IsFalse(shell.IsDatabaseRecoveryDialogOpen);
        Assert.IsTrue(shell.LoginViewModel.IsBackendInitialized);
        Assert.IsFalse(shell.IsDatabaseRecoveryBusy);
        Assert.AreEqual(0, runtime.ResetCalls);
        Assert.AreEqual(1, ((MainViewModelStartupTests.StartupEndpoints)(object)endpoints).RestoreCalls);
    }

    [TestMethod]
    public async Task RetryReclassifiesTemporaryKeyFailureWithoutContinuingToOfferReset()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (failure, kind) = CreateFailure(2);
        await using var runtime = new FailedRuntime(failure, kind);
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        runtime.SetFailure(new KeyProtectorUnavailableException(KeyProtectorUnavailableReason.DeviceLocked),
            BackendRuntimeFailureKind.PlatformKeyUnavailable);
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryRetryCommand);
        Assert.IsFalse(shell.IsDatabaseRecoveryPrimaryButtonVisible);
        Assert.AreEqual(preferences.GetString("Shell_PlatformKeyUnavailable"), shell.DatabaseRecoveryMessage);
        Assert.IsTrue(shell.CanRetryDatabaseStartup);
        Assert.AreEqual(0, runtime.ResetCalls);
    }

    [TestMethod]
    public async Task RepeatedUnchangedFailureDoesNotDiscardAnExplicitReview()
    {
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        var (failure, kind) = CreateFailure(3);
        await using var runtime = new FailedRuntime(failure, kind);
        using var shell = new MainViewModel(DispatchProxy.Create<IEndpoints, RejectAllEndpoints>(),
            runtime, new FakeBackgroundSyncSettingsClient(), preferences);
        await shell.InitializeAsync();
        await ExecuteAndWaitAsync(shell.DatabaseRecoveryPrimaryCommand);
        shell.IsDatabaseResetAcknowledged = true;
        typeof(MainViewModel).GetMethod("ShowDatabaseRecoveryDialog", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(shell, [new DatabaseUnreadableException()]);
        Assert.IsTrue(shell.IsDatabaseResetAcknowledgementVisible);
        Assert.IsTrue(shell.IsDatabaseResetAcknowledged);
        Assert.AreEqual(0, runtime.ResetCalls);
    }

    private static async Task ExecuteAndWaitAsync(ReactiveCommand<RxVoid, RxVoid> command)
    {
        // Await this invocation's completion instead of polling asynchronously
        // scheduled ICommand readiness between fire-and-forget executions.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var execution = command.Execute().Subscribe(new CommandCompletionObserver(completed));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class CommandCompletionObserver(TaskCompletionSource completed) : IObserver<RxVoid>
    {
        public void OnNext(RxVoid value) { }
        public void OnError(Exception error) => completed.TrySetException(error);
        public void OnCompleted() => completed.TrySetResult();
    }

    private static (DatabaseRecoveryRequiredException Failure, BackendRuntimeFailureKind Kind) CreateFailure(int value) => value switch
    {
        0 => (new DatabaseVersionNotSupportedException(99, 12, 12), BackendRuntimeFailureKind.DatabaseCompatibility),
        1 => (new DatabaseConfigurationInvalidException(), BackendRuntimeFailureKind.DatabaseConfigurationInvalid),
        2 => (new DatabaseKeyUnavailableException(), BackendRuntimeFailureKind.DatabaseKeyUnavailable),
        _ => (new DatabaseUnreadableException(), BackendRuntimeFailureKind.DatabaseUnreadable)
    };

    private sealed class FailedRuntime : IBackendRuntimeClient
    {
        private Exception _failure;
        public FailedRuntime(Exception failure, BackendRuntimeFailureKind kind)
        {
            _failure = failure;
            Snapshot = new BackendRuntimeSnapshot(
                kind == BackendRuntimeFailureKind.PlatformKeyUnavailable
                    ? BackendRuntimeState.WaitingForDeviceUnlock : BackendRuntimeState.Failed,
                kind, failure, DateTimeOffset.UtcNow);
        }

        public BackendRuntimeSnapshot Snapshot { get; private set; }
        public int ResetCalls { get; private set; }
        public int ConnectCalls { get; private set; }
        public bool RecoverOnConnect { get; set; }
        public Task? ResetRelease { get; init; }
        public Exception? ResetFailure { get; init; }
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            if (!RecoverOnConnect) return Task.FromException(_failure);
            Snapshot = new BackendRuntimeSnapshot(BackendRuntimeState.Ready, BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
            Snapshot.IsReady ? Task.CompletedTask : Task.FromException(_failure);
        public void SetFailure(Exception failure, BackendRuntimeFailureKind kind)
        {
            _failure = failure;
            Snapshot = new BackendRuntimeSnapshot(BackendRuntimeState.WaitingForDeviceUnlock, kind, failure, DateTimeOffset.UtcNow);
        }
        public async Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
        {
            ResetCalls++;
            if (ResetRelease is not null) await ResetRelease;
            if (ResetFailure is not null) throw ResetFailure;
            Snapshot = new BackendRuntimeSnapshot(BackendRuntimeState.NotStarted,
                BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public class RejectAllEndpoints : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("Recovery UI must not call vault endpoints.");
        }
    }

    private sealed class ShippedLocalizationLoader : IFrontendLocalizationResourceLoader
    {
        public ValueTask<Stream> OpenAsync(AppLanguage language, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(File.OpenRead(Path.Combine(AppContext.BaseDirectory,
                "Localization", language == AppLanguage.Hungarian ? "hu.json" : "en_us.json")));
    }
}
