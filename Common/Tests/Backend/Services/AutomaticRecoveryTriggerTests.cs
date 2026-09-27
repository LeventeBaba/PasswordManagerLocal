using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class AutomaticRecoveryTriggerTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Recovery")]
    public async Task LoginAsync_InvokesRecoveryBeforeSessionCreation()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var recovery = host.Services.GetRequiredService<FakeUserDataRecoveryCoordinator>();
        var registrationToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var userId = host.Services.GetRequiredService<ITokenService>().GetUidOrThrow(registrationToken);
        auth.LogoutUser(userId);
        recovery.Calls.Clear();

        _ = await auth.LoginAsync(host.CreateValidLoginRequest());

        var call = recovery.Calls.Single();
        MSTestAssert.AreEqual(userId, call.UserId);
        MSTestAssert.AreEqual(UserDataRecoveryTrigger.Login, call.Trigger);
        MSTestAssert.AreEqual(UserSyncKeyConfidence.UnconfirmedPassword, call.KeyConfidence);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Recovery")]
    public async Task InitializeRememberMeSessionAsync_InvokesRecoveryWithTrustedSavedKey()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var rememberMe = host.Services.GetRequiredService<IRememberMeService>();
        var recovery = host.Services.GetRequiredService<FakeUserDataRecoveryCoordinator>();
        var request = host.CreateValidRegistrationRequest();
        request.RememberMe = true;
        var registrationToken = await auth.RegisterAsync(request);
        var userId = host.Services.GetRequiredService<ITokenService>().GetUidOrThrow(registrationToken);
        auth.LogoutUser(userId);
        recovery.Calls.Clear();

        _ = await rememberMe.InitializeRememberMeSessionAsync(userId);

        var call = recovery.Calls.Single();
        MSTestAssert.AreEqual(userId, call.UserId);
        MSTestAssert.AreEqual(UserDataRecoveryTrigger.RememberMeStartup, call.Trigger);
        MSTestAssert.AreEqual(UserSyncKeyConfidence.RememberMe, call.KeyConfidence);
    }
}
