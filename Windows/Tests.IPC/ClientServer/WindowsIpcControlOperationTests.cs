using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Ipc.Client;
using PasswordManagerLocal.Windows.Ipc.Contracts;
using PasswordManagerLocal.Windows.Ipc.Protocol;
using PasswordManagerLocal.Windows.Ipc.Serialization;
using PasswordManagerLocal.Windows.Ipc.Server;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.ClientServer;

[TestClass]
public sealed class WindowsIpcControlOperationTests
{
    [TestMethod]
    public async Task UiActivationRequestUsesTypedContractWithoutAvaloniaTypes()
    {
        UiActivationRequestDto? observed = null;
        var sink = new DelegateUiActivationRequestSink((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            observed = request;
            return Task.FromResult(true);
        });
        await using var session = await IpcTestSession.CreateAsync(
            new IWindowsIpcRequestHandler[]
            {
                new RequestUiActivationWindowsIpcRequestHandler(sink)
            });
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);
        var request = new UiActivationRequestDto(
            UiActivationReason.Notification,
            BringToForeground: true);

        var response = await controlClient.RequestUiActivationAsync(request);

        Assert.IsTrue(response.Accepted);
        Assert.AreEqual(request, observed);
    }


    [TestMethod]
    public async Task ReloadApplicationPreferencesUsesNoPayload()
    {
        var handled = false;
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.ReloadApplicationPreferences,
            (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.EnsureNoPayload();
                handled = true;
                return Task.FromResult(context.Success(
                    new RequestAcceptedDto(true),
                    WindowsIpcJsonContext.Default.RequestAcceptedDto));
            });
        await using var session = await IpcTestSession.CreateAsync([handler]);
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);

        var response = await controlClient.ReloadApplicationPreferencesAsync();

        Assert.IsTrue(handled);
        Assert.IsTrue(response.Accepted);
    }

    [TestMethod]
    public async Task DatabaseCompatibilityStatusRoundTripsThroughControlIpc()
    {
        var changedAt = DateTimeOffset.UtcNow;
        var failure = new IpcFailureDto(
            IpcFailureKind.Runtime,
            "The local database version is not supported.",
            changedAt,
            IsRetryable: false,
            RequiresProcessRestart: false);
        var handler = new DelegateWindowsIpcRequestHandler(
            IpcOperationId.GetBackendRuntimeStatus,
            (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.EnsureNoPayload();
                return Task.FromResult(context.Success(
                    new BackendRuntimeStatusDto(
                        BackendRuntimeStatusState.Failed,
                        BackendRuntimeFailureStatusKind.DatabaseCompatibility,
                        failure,
                        RequiresProcessRestart: false,
                        changedAt,
                        new DatabaseCompatibilityStatusDto(
                            DetectedVersion: 99,
                            OldestSupportedVersion: 12,
                            CurrentVersion: 12)),
                    WindowsIpcJsonContext.Default.BackendRuntimeStatusDto));
            });
        await using var session = await IpcTestSession.CreateAsync([handler]);
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);

        var runtime = await controlClient.GetBackendRuntimeStatusAsync();

        Assert.AreEqual(BackendRuntimeFailureStatusKind.DatabaseCompatibility, runtime.FailureKind);
        Assert.IsNotNull(runtime.DatabaseCompatibility);
        Assert.AreEqual(99, runtime.DatabaseCompatibility.DetectedVersion);
        Assert.AreEqual(12, runtime.DatabaseCompatibility.OldestSupportedVersion);
        Assert.AreEqual(12, runtime.DatabaseCompatibility.CurrentVersion);
    }

    [TestMethod]
    public async Task RuntimeAndAgentStatusRepresentProcessRestartRequirement()
    {
        var changedAt = DateTimeOffset.UtcNow;
        var failure = new IpcFailureDto(
            IpcFailureKind.InteractiveCleanup,
            "The process must be restarted before the runtime can start again.",
            changedAt,
            IsRetryable: false,
            RequiresProcessRestart: true);
        var handlers = new IWindowsIpcRequestHandler[]
        {
            new DelegateWindowsIpcRequestHandler(
                IpcOperationId.GetBackendRuntimeStatus,
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.EnsureNoPayload();
                    return Task.FromResult(context.Success(
                        new BackendRuntimeStatusDto(
                            BackendRuntimeStatusState.Failed,
                            BackendRuntimeFailureStatusKind.InteractiveCleanupFailure,
                            failure,
                            RequiresProcessRestart: true,
                            changedAt),
                        WindowsIpcJsonContext.Default.BackendRuntimeStatusDto));
                }),
            new DelegateWindowsIpcRequestHandler(
                IpcOperationId.GetAgentStatus,
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.EnsureNoPayload();
                    return Task.FromResult(context.Success(
                        new AgentStatusDto(
                            AgentState.Failed,
                            AgentAdmissionState.Closed,
                            IsUiConnected: false,
                            BackendOwnedByAgent: false,
                            IsBackendRunning: false,
                            IsBackgroundSyncEnabled: true,
                            RequiresProcessRestart: true,
                            LastFailure: failure,
                            StartedAtUtc: null),
                        WindowsIpcJsonContext.Default.AgentStatusDto));
                })
        };
        await using var session = await IpcTestSession.CreateAsync(handlers);
        var controlClient = new WindowsIpcControlClient(session.Client, session.Serializer);

        var runtime = await controlClient.GetBackendRuntimeStatusAsync();
        var agent = await controlClient.GetAgentStatusAsync();

        Assert.IsTrue(runtime.RequiresProcessRestart);
        Assert.IsTrue(agent.RequiresProcessRestart);
        Assert.IsNotNull(agent.LastFailure);
        Assert.IsTrue(agent.LastFailure.RequiresProcessRestart);
        Assert.IsNull(agent.StartedAtUtc);
    }
}
