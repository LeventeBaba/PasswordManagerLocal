using System.Reflection;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Frontend.Localization;
using PasswordManagerLocal.Common.Frontend.ViewModels;
using PasswordManagerLocal.Common.Tests.Fakes;

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

        await ExecuteWhenReadyAsync(shell.DatabaseRecoveryPrimaryCommand);
        Assert.AreEqual(0, runtime.ResetCalls, "The first action must only show permanent-erasure confirmation.");
        await ExecuteWhenReadyAsync(shell.DatabaseRecoverySecondaryCommand);
        Assert.AreEqual(title, shell.DatabaseRecoveryTitle);
        Assert.AreEqual(originalMessage, shell.DatabaseRecoveryMessage);

        await ExecuteWhenReadyAsync(shell.DatabaseRecoveryPrimaryCommand);
        await ExecuteWhenReadyAsync(shell.DatabaseRecoveryPrimaryCommand);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (runtime.ResetCalls != 1 || shell.IsDatabaseRecoveryDialogOpen)
            await Task.Delay(10, timeout.Token);

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

    private static async Task ExecuteWhenReadyAsync(ICommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!command.CanExecute(null))
            await Task.Delay(10, timeout.Token);
        command.Execute(null);
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
        private readonly Exception _failure;
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
        public event EventHandler<BackendRuntimeStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.FromException(_failure);
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Task.FromException(_failure);
        public Task ResetDatabaseAndRestartAsync(CancellationToken cancellationToken = default)
        {
            ResetCalls++;
            Snapshot = new BackendRuntimeSnapshot(BackendRuntimeState.NotStarted,
                BackendRuntimeFailureKind.None, null, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
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
