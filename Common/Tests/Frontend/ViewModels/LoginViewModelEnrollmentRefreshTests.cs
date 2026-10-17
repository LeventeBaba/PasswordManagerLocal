using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Enrollment;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Frontend.Services;
using PasswordManagerLocal.Common.Frontend.ViewModels.Auth;
using PasswordManagerLocal.Common.Preferences;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class LoginViewModelEnrollmentRefreshTests
{
    [TestMethod]
    public async Task ExpirationDeadlineQueriesOnceAndAppliesAuthoritativeExpiredState()
    {
        var scheduler = new PasswordManagerLocal.Common.Tests.Fakes.ManualFrontendDeadlineScheduler();
        var endpoints = CreateEndpoints();
        var recording = (EnrollmentStatusEndpoints)(object)endpoints;
        recording.StatusResponse = new DeviceEnrollmentStatusResponse
        {
            State = DeviceEnrollmentState.Expired,
            ErrorCode = DeviceEnrollmentErrorCode.CodeExpired,
            ExpiresAt = scheduler.NowUtc.AddMinutes(10)
        };
        using var viewModel = CreateViewModel(endpoints, scheduler);
        SetPrivateField(viewModel, "_isDeviceTransferCodeVisible", true);
        var expiresAt = scheduler.NowUtc.AddMinutes(10);
        ScheduleEnrollmentExpiration(viewModel, expiresAt);

        scheduler.AdvanceTo(expiresAt);
        await recording.StatusCall.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, recording.StatusCalls);
        Assert.IsTrue(viewModel.IsDeviceTransferFinished);
        Assert.IsFalse(viewModel.IsDeviceTransferCodeVisible);
        Assert.AreEqual(0, scheduler.ActiveCount);
    }

    [TestMethod]
    public async Task AbandoningEnrollmentCancelsItsTimerAndBackendSession()
    {
        var scheduler = new PasswordManagerLocal.Common.Tests.Fakes.ManualFrontendDeadlineScheduler();
        var endpoints = CreateEndpoints();
        var recording = (EnrollmentStatusEndpoints)(object)endpoints;
        using var viewModel = CreateViewModel(endpoints, scheduler);
        SetPrivateField(viewModel, "_isDeviceTransferCodeVisible", true);
        var expiresAt = scheduler.NowUtc.AddMinutes(10);
        ScheduleEnrollmentExpiration(viewModel, expiresAt);
        Assert.AreEqual(1, scheduler.ActiveCount);

        viewModel.OnNavigatedFrom();
        await recording.CancelCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.AdvanceTo(expiresAt);

        Assert.AreEqual(1, recording.CancelCalls);
        Assert.AreEqual(0, recording.StatusCalls);
        Assert.AreEqual(0, scheduler.ActiveCount);
        Assert.IsFalse(viewModel.IsDeviceTransferCodeVisible);
    }

    [TestMethod]
    public async Task ResumeReconciliationClosesEnrollmentForTerminalOrMissingServiceState()
    {
        foreach (var terminalState in new[]
                 {
                     DeviceEnrollmentState.None,
                     DeviceEnrollmentState.Cancelled,
                     DeviceEnrollmentState.Completed,
                     DeviceEnrollmentState.Failed,
                     DeviceEnrollmentState.Expired
                 })
        {
            var endpoints = CreateEndpoints();
            var recording = (EnrollmentStatusEndpoints)(object)endpoints;
            recording.StatusResponse = new DeviceEnrollmentStatusResponse { State = terminalState };
            using var viewModel = CreateViewModel(
                endpoints,
                new PasswordManagerLocal.Common.Tests.Fakes.ManualFrontendDeadlineScheduler());
            SetPrivateField(viewModel, "_isDeviceTransferCodeVisible", true);

            Assert.IsTrue(await viewModel.RefreshEnrollmentAutomaticallyAsync(() => true, CancellationToken.None));
            Assert.AreEqual(1, recording.StatusCalls);
            Assert.IsTrue(viewModel.IsDeviceTransferFinished, terminalState.ToString());
            Assert.IsFalse(viewModel.IsDeviceTransferCodeVisible, terminalState.ToString());
        }
    }

    private static LoginViewModel CreateViewModel(
        IEndpoints endpoints,
        IFrontendDeadlineScheduler scheduler)
    {
        var preferences = new UiPreferencesService(
            new InMemoryApplicationPreferencesStore(new ApplicationPreferences
            {
                SchemaVersion = ApplicationPreferences.CurrentSchemaVersion,
                Language = AppLanguage.English,
                Theme = AppThemeMode.Dark
            }));
        return new LoginViewModel(preferences, endpoints, () => { }, _ => Task.CompletedTask, scheduler);
    }

    private static IEndpoints CreateEndpoints() =>
        DispatchProxy.Create<IEndpoints, EnrollmentStatusEndpoints>();

    private static void SetPrivateField(LoginViewModel viewModel, string fieldName, object value)
    {
        var field = typeof(LoginViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(LoginViewModel).FullName, fieldName);
        field.SetValue(viewModel, value);
    }

    private static void ScheduleEnrollmentExpiration(LoginViewModel viewModel, DateTimeOffset expiresAt)
    {
        var method = typeof(LoginViewModel).GetMethod(
            "ScheduleEnrollmentExpiration",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(LoginViewModel).FullName, "ScheduleEnrollmentExpiration");
        method.Invoke(viewModel, [expiresAt]);
    }

    public class EnrollmentStatusEndpoints : DispatchProxy
    {
        public DeviceEnrollmentStatusResponse StatusResponse { get; set; } = new() { State = DeviceEnrollmentState.Waiting };
        public int StatusCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public TaskCompletionSource StatusCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IEndpoints.GetDeviceEnrollmentStatusAsync):
                    StatusCalls++;
                    StatusCall.TrySetResult();
                    return Task.FromResult(StatusResponse);
                case nameof(IEndpoints.CancelDeviceEnrollmentAsync):
                    CancelCalls++;
                    CancelCall.TrySetResult();
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException($"Unexpected endpoint call: {targetMethod?.Name}.");
            }
        }
    }
}
