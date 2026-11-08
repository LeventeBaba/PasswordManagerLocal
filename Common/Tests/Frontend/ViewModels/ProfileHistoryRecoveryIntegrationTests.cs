using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Frontend.ViewModels.Pages;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
[DoNotParallelize]
public sealed class ProfileHistoryRecoveryIntegrationTests
{
    [TestMethod]
    [DataRow(false, "P@ssw0rd12345678")]
    [DataRow(true, "  Árvíztűrő🔑_12345  ")]
    [Timeout(120_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task RecoveryConfirmationUsesTheRegisteredCredential_AndRejectsAnIncorrectPassword(
        bool restoreRememberedSession, string masterPassword)
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var removed = await ProductionSyncTestHost.CreateAsync();
        // Match the real registration/login UI contract, rather than the raw UTF-8
        // fixture credentials used by backend-only tests.
        var credential = SHA512.HashData(Encoding.UTF8.GetBytes(masterPassword));
        try
        {
            var request = source.CreateRegistrationRequest("recovery_credential");
            request.Password = credential;
            request.RememberMe = restoreRememberedSession;
            var token = await source.Endpoints.RegisterAsync(request);
            Guid userId;
            using (var scope = source.Services.CreateScope())
                userId = (await scope.ServiceProvider.GetRequiredService<IUserLookupService>().GetAndVerifyUserAsync(token)).UId;
            if (restoreRememberedSession)
            {
                await source.Endpoints.LogoutAsync(token);
                token = await source.Endpoints.InitializeRememberMeSessionAsync(userId);
            }

            var secret = Encoding.UTF8.GetBytes("PreservedVaultSecret123!");
            await source.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest { Name = "Keep", Password = secret });
            source.ConnectTo(removed);
            removed.ConnectTo(source);
            await source.EnableSyncAsync(token);
            var code = await removed.Endpoints.StartDeviceEnrollmentAsync();
            await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
            await source.Endpoints.DisconnectUserDeviceAsync(token, removed.Identity.LocalDeviceId, credential);
            using (var scope = source.Services.CreateScope())
                await RecoveryHistoryTestData.BreakRemovedDeviceHistoryAsync(scope.ServiceProvider, userId, removed.Identity.LocalDeviceId);

            await using var preferences = await TestUiPreferencesFactory.CreateAsync();
            using var model = new ProfileViewModel(preferences, source.Endpoints, () => Task.FromResult(true), () => Task.CompletedTask);
            model.SetSessionToken(token);
            Assert.IsTrue(await model.EnsureDevicesLoadedAsync());
            await InvokeAsync(model, "BeginViewDeviceAsync", model.Devices.Single(device => device.IsCurrentDevice));
            await InvokeAsync(model, "RunSelfDiagnosticsAsync");
            Assert.IsTrue(model.IsHistoryRecoveryDialogOpen);
            long previousKeyEpoch;
            using (var scope = source.Services.CreateScope())
                previousKeyEpoch = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(userId))!.KeyEpoch;

            model.HistoryRecoveryPassword = "IncorrectPassword!";
            model.IsHistoryRecoveryAcknowledged = true;
            await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
            Assert.IsTrue(model.IsHistoryRecoveryDialogOpen);
            StringAssert.Contains(model.HistoryRecoveryFeedback, preferences.GetString("Profile_Device_SelfDiagnostics_RecoveryPasswordInvalid"));
            Assert.AreEqual(string.Empty, model.HistoryRecoveryPassword);
            using (var scope = source.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var user = await services.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(userId);
                Assert.AreEqual(previousKeyEpoch, user!.KeyEpoch);
                var journals = services.GetRequiredService<IUserRecoveryJournalStore>();
                foreach (var id in await journals.ListIdsAsync(default))
                    Assert.AreNotEqual<Guid?>(userId, await journals.ReadUserIdAsync(id, default));
            }

            model.HistoryRecoveryPassword = masterPassword;
            model.IsHistoryRecoveryAcknowledged = true;
            await InvokeAsync(model, "ConfirmHistoryRecoveryAsync");
            Assert.IsFalse(model.IsStatusMessageError, model.StatusMessage);
            Assert.IsFalse(model.IsHistoryRecoveryDialogOpen, model.HistoryRecoveryFeedback);
            Assert.IsFalse(model.HasHistoryRecoveryOffer);
            Assert.AreEqual(string.Empty, model.HistoryRecoveryPassword);
            var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
            Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
            var saved = (await source.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Single();
            CollectionAssert.AreEqual(secret, await source.Endpoints.GetUnsecurePasswordAsync(token, saved.Id));
            using (var scope = source.Services.CreateScope())
                Assert.IsTrue((await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(userId))!.KeyEpoch > previousKeyEpoch);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static Task InvokeAsync(ProfileViewModel model, string method, params object[] args) =>
        (Task)typeof(ProfileViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, args)!;
}
