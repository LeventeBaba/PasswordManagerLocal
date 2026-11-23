using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Frontend.ViewModels.Auth;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Frontend.ViewModels;

[TestClass]
public sealed class LocalProfileCleanupConfirmationTests
{
    [TestMethod]
    [TestCategory("LocalProfileCleanup")]
    [TestCategory("EnrollmentSecurity")]
    public async Task Cleanup_RequiresFreshAcknowledgementAndClearsTheCredentialBuffer()
    {
        var endpoints = DispatchProxy.Create<IEndpoints, CleanupEndpoints>();
        var recorder = (CleanupEndpoints)(object)endpoints;
        var preferences = await TestUiPreferencesFactory.CreateAsync();
        using var model = new LoginViewModel(preferences, endpoints, () => { }, _ => Task.CompletedTask);
        typeof(LoginViewModel).GetProperty(nameof(LoginViewModel.IsBackendInitialized))!.SetValue(model, true);
        typeof(LoginViewModel).GetProperty(nameof(LoginViewModel.IsLocalProfileRemovalVisible))!.SetValue(model, true);
        model.Username = "cleanup_profile"; model.Password = "P@ssw0rd12345678";
        var remove = typeof(LoginViewModel).GetMethod("RemoveLocalProfileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)remove.Invoke(model, null)!;
        Assert.AreEqual(0, recorder.Calls);
        model.IsLocalProfileRemovalAcknowledged = true;
        model.Username = "another_profile";
        Assert.IsFalse(model.CanRemoveLocalProfile, "Changing the selected profile must invalidate acknowledgement.");
        model.IsLocalProfileRemovalAcknowledged = true;
        await (Task)remove.Invoke(model, null)!;
        Assert.AreEqual(1, recorder.Calls);
        Assert.AreEqual("another_profile", recorder.Username);
        Assert.IsTrue(recorder.Acknowledged);
        Assert.IsNotNull(recorder.PasswordBuffer);
        Assert.IsTrue(recorder.PasswordBuffer.All(value => value == 0));
        Assert.AreEqual(string.Empty, model.Password);
        Assert.IsFalse(model.IsLocalProfileRemovalAcknowledged);
        Assert.IsFalse(model.IsLocalProfileRemovalVisible);
    }

    public class CleanupEndpoints : DispatchProxy
    {
        public int Calls { get; private set; }
        public string? Username { get; private set; }
        public bool Acknowledged { get; private set; }
        public byte[]? PasswordBuffer { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name != nameof(IEndpoints.RemoveLocalProfileAsync))
                throw new InvalidOperationException("Cleanup must never invoke enrollment or another endpoint.");
            var request = (LoginRequest)arguments![0]!;
            Calls++; Username = request.Username; PasswordBuffer = request.Password;
            Acknowledged = (bool)arguments[1]!;
            return Task.CompletedTask;
        }
    }
}
