using global::PasswordManagerLocal.Common.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Linq;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class RememberMeServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SetRememberMe_ThenInitialize_ReturnsTokenWithKey()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));
        var remember = (IRememberMeService)host.Services.GetRequiredService(typeof(IRememberMeService));
        var keys = (IKeyVaultService)host.Services.GetRequiredService(typeof(IKeyVaultService));
        var tokens = (ITokenService)host.Services.GetRequiredService(typeof(ITokenService));
        var repo = (IUserRepository)host.Services.GetRequiredService(typeof(IUserRepository));

        var reg = host.CreateValidRegistrationRequest("carol");
        reg.RememberMe = false;

        var token = await auth.RegisterAsync(reg);

        MSTestAssert.AreNotEqual(Guid.Empty, token);
        MSTestAssert.IsTrue(tokens.TryGetUid(token, out var uid));
        MSTestAssert.AreNotEqual(Guid.Empty, uid);

        await remember.SetRememberMeAsync(token, true);

        var users = await repo.ListAllAsync();
        var carol = users.Single(u => u.UId == uid);
        MSTestAssert.IsNotNull(carol.SavedKey);
        MSTestAssert.IsNotEmpty(carol.SavedKey);

        var issued = await remember.RestoreRememberedSessionsAsync();

        MSTestAssert.HasCount(1, issued);

        var issuedToken = issued[0];
        MSTestAssert.AreNotEqual(Guid.Empty, issuedToken);
        MSTestAssert.IsTrue(tokens.Validate(issuedToken));
        MSTestAssert.IsTrue(keys.HasUserKey(issuedToken));

        MSTestAssert.IsTrue(tokens.TryGetUid(issuedToken, out var uid2));
        MSTestAssert.AreEqual(uid, uid2);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task TemporaryKeyUnavailability_DoesNotClearSavedKey()
    {
        var protector = new SwitchableKeyProtector();
        using var host = new BackendTestHost(keyProtector: protector);
        var auth = host.Services.GetRequiredService<IAuthService>();
        var remember = host.Services.GetRequiredService<IRememberMeService>();
        var users = host.Services.GetRequiredService<IUserRepository>();
        var registration = host.CreateValidRegistrationRequest("remember_locked_device");
        registration.RememberMe = true;
        var token = await auth.RegisterAsync(registration);
        var userId = host.Services.GetRequiredService<IUserService>().GetUidFromToken(token);
        var savedKeyBefore = (await users.ListAllAsync())
            .Single(user => user.UId == userId)
            .SavedKey!
            .ToArray();

        protector.IsTemporarilyUnavailable = true;

        await MSTestAssert.ThrowsAsync<KeyProtectorUnavailableException>(
            async () => await remember.RestoreRememberedSessionsAsync());

        var savedKeyAfter = (await users.ListAllAsync())
            .Single(user => user.UId == userId)
            .SavedKey;
        MSTestAssert.IsNotNull(savedKeyAfter);
        CollectionAssert.AreEqual(savedKeyBefore, savedKeyAfter);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task InitializeRememberMeSession_UpdatesCurrentDeviceLastLoginDate()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var remember = host.Services.GetRequiredService<IRememberMeService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var devices = host.Services.GetRequiredService<IDeviceService>();
        var identity = host.Services.GetRequiredService<IDeviceIdentityService>();

        var registration = host.CreateValidRegistrationRequest("remember_login_date");
        registration.RememberMe = true;
        var token = await auth.RegisterAsync(registration);
        var userId = users.GetUidFromToken(token);

        var bundle = await users.GetLoadAndVerifyUserDataBundleAsync(token);
        var localDevice = bundle.UserDevicesData.Devices.Single(device => device.Id == identity.LocalDeviceId);
        var oldLoginDate = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        localDevice.LastLoginDate = oldLoginDate;
        localDevice.LastUpdatedAt = new DateTimeOffset(oldLoginDate);
        localDevice.GenerateIntegrityHash();
        await users.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false);

        var restoredToken = await remember.InitializeRememberMeSessionAsync(userId);
        var restoredBundle = await users.GetLoadAndVerifyUserDataBundleAsync(restoredToken);
        var restoredLocalDevice = restoredBundle.UserDevicesData.Devices.Single(device => device.Id == identity.LocalDeviceId);
        var currentDeviceResponse = (await devices.GetUserDevicesAsync(restoredToken)).Single(device => device.IsCurrentDevice);

        MSTestAssert.IsTrue(restoredLocalDevice.LastLoginDate > oldLoginDate);
        MSTestAssert.AreEqual(oldLoginDate, restoredLocalDevice.PreviousLoginDate);
        MSTestAssert.IsNotNull(currentDeviceResponse.LastLoginDate);
        MSTestAssert.AreEqual(restoredLocalDevice.LastLoginDate, currentDeviceResponse.LastLoginDate.Value);
        MSTestAssert.AreEqual(oldLoginDate, currentDeviceResponse.PreviousLoginDate);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task DisableRememberMe_ClearsSavedKeyAndPreventsStartupSession()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var remember = host.Services.GetRequiredService<IRememberMeService>();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var repo = host.Services.GetRequiredService<IUserRepository>();
        var registration = host.CreateValidRegistrationRequest("remember_disable_user");
        registration.RememberMe = true;
        var token = await auth.RegisterAsync(registration);
        MSTestAssert.IsTrue(tokens.TryGetUid(token, out var userId));

        await remember.SetRememberMeAsync(token, false);

        var user = (await repo.ListAllAsync()).Single(item => item.UId == userId);
        MSTestAssert.IsNull(user.SavedKey);
        var initialized = await remember.RestoreRememberedSessionsAsync();
        MSTestAssert.HasCount(0, initialized);
    }
}