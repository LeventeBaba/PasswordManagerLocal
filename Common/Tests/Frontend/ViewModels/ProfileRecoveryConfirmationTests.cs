using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class ProfileRecoveryConfirmationTests
{
    [TestMethod]
    public async Task RecoveryRequiresAcknowledgementAndExplicitConfirmation_AndCancelPreservesTheOffer()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, RecoveryEndpoints>();
        var recording = (RecoveryEndpoints)(object)endpoints;
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var model = new ProfileViewModel(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        var token = Guid.NewGuid();
        await OpenAssessmentAsync(model, token);
        Assert.IsTrue(model.IsHistoryRecoveryDialogOpen);
        Assert.IsFalse(model.CanConfirmHistoryRecovery);
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.AreEqual(0, recording.ConfirmCalls);
        model.HistoryRecoveryPassword = "P@ssw0rd12345678";
        model.IsHistoryRecoveryAcknowledged = true;
        await model.ConfirmOpenDialogAsync();
        Assert.AreEqual(0, recording.ConfirmCalls, "Global Enter must not approve recovery.");
        Invoke(model, "CancelHistoryRecovery");
        Assert.IsFalse(model.IsHistoryRecoveryDialogOpen);
        Assert.IsTrue(model.HasHistoryRecoveryOffer);
        Invoke(model, "OpenHistoryRecoveryDialog");
        Assert.IsFalse(model.IsHistoryRecoveryAcknowledged);
        model.HistoryRecoveryPassword = "P@ssw0rd12345678";
        model.IsHistoryRecoveryAcknowledged = true;
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.AreEqual(1, recording.ConfirmCalls);
        Assert.AreEqual(token, recording.LastToken);
        Assert.AreEqual(new string('A', 64), recording.LastPlanId);
        CollectionAssert.AreEqual(
            System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes("P@ssw0rd12345678")),
            recording.SentPassword!);
        Assert.IsTrue(recording.BorrowedPassword!.All(value => value == 0));
        Assert.AreEqual(string.Empty, model.HistoryRecoveryPassword);
        Assert.IsFalse(model.IsHistoryRecoveryDialogOpen);
        Assert.IsFalse(model.HasHistoryRecoveryOffer);
    }

    [TestMethod]
    public async Task SessionChangeClearsAnAcknowledgedRecoveryPlan()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, RecoveryEndpoints>();
        var recording = (RecoveryEndpoints)(object)endpoints;
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var model = new ProfileViewModel(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        await OpenAssessmentAsync(model, Guid.NewGuid());
        model.HistoryRecoveryPassword = "P@ssw0rd12345678";
        model.IsHistoryRecoveryAcknowledged = true;
        model.SetSessionToken(Guid.NewGuid());
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.AreEqual(0, recording.ConfirmCalls);
        Assert.IsFalse(model.IsHistoryRecoveryDialogOpen);
        Assert.IsFalse(model.HasHistoryRecoveryOffer);
        Assert.IsFalse(model.IsHistoryRecoveryAcknowledged);
        Assert.AreEqual(string.Empty, model.HistoryRecoveryPassword);
    }

    [TestMethod]
    public async Task UpdatedBackendPlanRequiresANewAcknowledgement()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, RecoveryEndpoints>();
        var recording = (RecoveryEndpoints)(object)endpoints;
        recording.ConfirmationResult = new SelfDiagnosticsResultResponse
        {
            Healthy = false, RecoveryPlanId = new string('B', 64),
            Findings = ["RecoveryPlanChanged"], RecoveryRisks = ["RecoveryRiskUnmergedData"]
        };
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var model = new ProfileViewModel(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        await OpenAssessmentAsync(model, Guid.NewGuid());
        model.HistoryRecoveryPassword = "P@ssw0rd12345678";
        model.IsHistoryRecoveryAcknowledged = true;
        var dialogClosed = false;
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(model.IsHistoryRecoveryDialogOpen) && !model.IsHistoryRecoveryDialogOpen)
                dialogClosed = true;
        };
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.AreEqual(1, recording.ConfirmCalls);
        Assert.IsTrue(model.IsHistoryRecoveryDialogOpen);
        Assert.IsFalse(dialogClosed, "A retry must update the existing dialog without flashing it closed.");
        Assert.IsTrue(model.HasHistoryRecoveryFeedback);
        StringAssert.Contains(model.HistoryRecoveryFeedback, preferences.GetString("Profile_Device_SelfDiagnostics_RecoveryPlanChanged"));
        Assert.IsFalse(model.IsHistoryRecoveryAcknowledged);
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.AreEqual(1, recording.ConfirmCalls);
    }

    [TestMethod]
    public async Task InvalidPasswordIsExplainedInTheOpenDialog_AndRetryCanComplete()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, RecoveryEndpoints>();
        var recording = (RecoveryEndpoints)(object)endpoints;
        recording.ConfirmationResult = new SelfDiagnosticsResultResponse
        {
            Healthy = false, RecoveryPlanId = new string('A', 64), Findings = ["RecoveryPasswordInvalid"]
        };
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var model = new ProfileViewModel(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        await OpenAssessmentAsync(model, Guid.NewGuid());
        model.HistoryRecoveryPassword = "IncorrectPassword!";
        model.IsHistoryRecoveryAcknowledged = true;
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.IsTrue(model.IsHistoryRecoveryDialogOpen);
        Assert.IsTrue(model.HasHistoryRecoveryFeedback);
        StringAssert.Contains(model.HistoryRecoveryFeedback, preferences.GetString("Profile_Device_SelfDiagnostics_RecoveryPasswordInvalid"));
        Assert.IsFalse(model.IsHistoryRecoveryAcknowledged);
        Assert.AreEqual(string.Empty, model.HistoryRecoveryPassword);
        Assert.IsTrue(recording.BorrowedPassword!.All(value => value == 0));

        recording.ConfirmationResult = new SelfDiagnosticsResultResponse { Healthy = true, RepairedCount = 1 };
        model.HistoryRecoveryPassword = "P@ssw0rd12345678";
        model.IsHistoryRecoveryAcknowledged = true;
        Assert.IsTrue(model.CanConfirmHistoryRecovery);
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.AreEqual(2, recording.ConfirmCalls);
        Assert.IsFalse(model.IsHistoryRecoveryDialogOpen);
        Assert.IsFalse(model.HasHistoryRecoveryFeedback);
    }

    [TestMethod]
    public async Task EndpointFailureKeepsRecoveryOpenAndExposesTheSafeError()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, RecoveryEndpoints>();
        var recording = (RecoveryEndpoints)(object)endpoints;
        recording.ConfirmationException = new IOException("Internal details must not be shown.");
        await using var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var model = new ProfileViewModel(preferences, endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
        await OpenAssessmentAsync(model, Guid.NewGuid());
        model.HistoryRecoveryPassword = "P@ssw0rd12345678";
        model.IsHistoryRecoveryAcknowledged = true;
        await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
        Assert.IsTrue(model.IsHistoryRecoveryDialogOpen);
        Assert.IsTrue(model.IsStatusMessageError);
        Assert.AreEqual(preferences.GetString("Error_Generic"), model.StatusMessage);
        Assert.IsFalse(model.IsRunningSelfDiagnostics);
        Assert.IsTrue(recording.BorrowedPassword!.All(value => value == 0));
    }

    private static async Task OpenAssessmentAsync(ProfileViewModel model, Guid token)
    {
        model.SetSessionToken(token);
        Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
        await InvokeAsync(model, "BeginViewDeviceAsync", model.Devices.Single());
        await InvokeAsync(model, "RunSelfDiagnosticsAsync");
    }
    private static void Invoke(ProfileViewModel model, string method) => typeof(ProfileViewModel)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
    private static Task InvokeAsync(ProfileViewModel model, string method, params object[] args) => (Task)typeof(ProfileViewModel)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args)!;

    public class RecoveryEndpoints : DispatchProxy
    {
        public int ConfirmCalls { get; private set; }
        public Guid LastToken { get; private set; }
        public string? LastPlanId { get; private set; }
        public byte[]? SentPassword { get; private set; }
        public byte[]? BorrowedPassword { get; private set; }
        public Exception? ConfirmationException { get; set; }
        public SelfDiagnosticsResultResponse ConfirmationResult { get; set; } = new()
        {
            Healthy = true, RepairedCount = 1, Findings = ["ConfirmedHistoryRecovery"], RecoveryJournalId = Guid.NewGuid()
        };
        private readonly Guid _deviceId = Guid.NewGuid();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(IEndpoints.GetUserDevicesAsync):
                    return Task.FromResult<IReadOnlyList<UserDeviceInfoResponse>>([new()
                    {
                        DeviceId = _deviceId, Name = "This device", IsCurrentDevice = true, IsSyncOn = true
                    }]);
                case nameof(IEndpoints.RunSelfDiagnosticsAndRepairAsync):
                    return Task.FromResult(new SelfDiagnosticsResultResponse
                    {
                        Healthy = false, RecoveryPlanId = new string('A', 64),
                        RecoveryRisks = ["RecoveryRiskOtherDevices"], RecoveryAffectedDevices = 1
                    });
                case nameof(IEndpoints.ConfirmSelfDiagnosticsRepairAsync):
                    ConfirmCalls++; LastToken = (Guid)args![0]!; LastPlanId = (string)args[1]!;
                    BorrowedPassword = (byte[])args[2]!;
                    SentPassword = BorrowedPassword.ToArray();
                    return ConfirmationException is { } error
                        ? Task.FromException<SelfDiagnosticsResultResponse>(error)
                        : Task.FromResult(ConfirmationResult);
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}
