using global::PasswordManagerLocal.Common.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class AuthServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    [TestCategory("Security")]
    public async Task Register_CreatesGenesisAuthorizationAndExactLocalSequencingState()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var userRepository = host.Services.GetRequiredService<IUserRepository>();
        var identity = host.Services.GetRequiredService<IDeviceIdentityService>();
        var authorizations = host.Services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        var controlStates = host.Services.GetRequiredService<IUserControlStateRepository>();
        var syncStates = host.Services.GetRequiredService<IUserSyncStateRepository>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("genesis_member"));
        var userId = users.GetUidFromToken(token);
        var user = await userRepository.GetByIdAsync(userId);
        var authorization = await authorizations.GetActiveAsync(userId, identity.LocalDeviceId, identity.OriginInstanceId);
        var controlState = await controlStates.GetAsync(userId);
        var syncState = await syncStates.GetAsync(userId);

        MSTestAssert.IsNotNull(user);
        MSTestAssert.AreEqual(1L, user.MembershipEpoch);
        MSTestAssert.IsNotNull(authorization);
        MSTestAssert.IsTrue(authorization.IsGenesis);
        MSTestAssert.AreEqual(identity.OriginInstanceId, authorization.OriginInstanceId);
        CollectionAssert.AreEqual(identity.SignPublicKey, authorization.SignPublicKey);
        MSTestAssert.IsNotNull(controlState);
        MSTestAssert.AreEqual(identity.OriginInstanceId, controlState.LocalOriginInstanceId);
        MSTestAssert.AreEqual(1L, controlState.NextOriginSequence);
        MSTestAssert.AreEqual(user.KeyEpoch, controlState.AppliedKeyEpoch);
        MSTestAssert.AreEqual(1L, controlState.AppliedMembershipEpoch);
        MSTestAssert.IsNotNull(syncState);
        MSTestAssert.AreEqual(identity.OriginInstanceId, syncState.LocalOriginInstanceId);
        MSTestAssert.AreEqual(1L, syncState.NextOriginRevision);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Register_ThenLogin_Roundtrip_Works()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));
        var cache = (IDataCachingService)host.Services.GetRequiredService(typeof(IDataCachingService));
        var keys = (IKeyVaultService)host.Services.GetRequiredService(typeof(IKeyVaultService));
        var tokens = (ITokenService)host.Services.GetRequiredService(typeof(ITokenService));
        var identity = host.Services.GetRequiredService<IDeviceIdentityService>();

        var reg = host.CreateValidRegistrationRequest("alice");
        var token1 = await auth.RegisterAsync(reg);

        MSTestAssert.AreNotEqual(Guid.Empty, token1);
        MSTestAssert.IsTrue(tokens.Validate(token1));
        MSTestAssert.IsTrue(keys.HasUserKey(token1));
        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(token1, out var ud1));
        MSTestAssert.IsNotNull(ud1);
        MSTestAssert.AreEqual("alice", ud1.GeneralUserData.Username);
        var registrationDevice = ud1.UserDevicesData.Devices.Single(device => device.Id == identity.LocalDeviceId);
        var registrationLoginDate = registrationDevice.LastLoginDate;
        MSTestAssert.IsNull(registrationDevice.PreviousLoginDate);

        var login = host.CreateValidLoginRequest("alice");
        var token2 = await auth.LoginAsync(login);

        MSTestAssert.AreNotEqual(Guid.Empty, token2);
        MSTestAssert.IsTrue(tokens.Validate(token2));
        MSTestAssert.IsTrue(keys.HasUserKey(token2));
        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(token2, out var ud2));
        MSTestAssert.IsNotNull(ud2);
        MSTestAssert.AreEqual("alice", ud2.GeneralUserData.Username);
        var loginDevice = ud2.UserDevicesData.Devices.Single(device => device.Id == identity.LocalDeviceId);
        MSTestAssert.IsNotNull(loginDevice.PreviousLoginDate);
        MSTestAssert.AreEqual(registrationLoginDate, loginDevice.PreviousLoginDate.Value);
        MSTestAssert.IsTrue(loginDevice.LastLoginDate >= registrationLoginDate);

        MSTestAssert.AreNotEqual(token1, token2);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Login_WrongPassword_DoesNotCreateSessionState()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var tokens = host.Services.GetRequiredService<ITokenService>();

        var registrationToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("failed_login"));
        var uid = users.GetUidFromToken(registrationToken);
        var tokensBefore = tokens.ListTokensByUid(uid).OrderBy(token => token).ToArray();

        var request = host.CreateValidLoginRequest("failed_login");
        request.Password = Encoding.UTF8.GetBytes("WrongPassword12345678");

        await ExpectThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync(request));

        var tokensAfter = tokens.ListTokensByUid(uid).OrderBy(token => token).ToArray();
        CollectionAssert.AreEqual(tokensBefore, tokensAfter);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Register_DuplicateUsername_Throws()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));

        await auth.RegisterAsync(host.CreateValidRegistrationRequest("bob"));

        await ExpectThrowsAsync<InvalidInputException>(async () =>
        {
            await auth.RegisterAsync(host.CreateValidRegistrationRequest("bob"));
        });
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void Logout_InvalidToken_Throws()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));

        ExpectThrows<InvalidTokenException>(() =>
        {
            auth.Logout(Guid.NewGuid());
        });
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Logout_ValidToken_InvalidatesEverything()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));
        var cache = (IDataCachingService)host.Services.GetRequiredService(typeof(IDataCachingService));
        var keys = (IKeyVaultService)host.Services.GetRequiredService(typeof(IKeyVaultService));
        var tokens = (ITokenService)host.Services.GetRequiredService(typeof(ITokenService));

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("charlie"));

        MSTestAssert.IsTrue(tokens.Validate(token));
        MSTestAssert.IsTrue(keys.HasUserKey(token));
        MSTestAssert.IsTrue(cache.TryGetUserData(token, out _));

        auth.Logout(token);

        MSTestAssert.IsFalse(tokens.Validate(token));
        MSTestAssert.IsFalse(keys.HasUserKey(token));
        MSTestAssert.IsFalse(cache.TryGetUserData(token, out _));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ChangeMasterPassword_ValidRequest_Works()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("dave"));

        var newPassword = Encoding.UTF8.GetBytes("N3wP@ssw0rd_123456");

        var request = new MasterPasswordChangeRequest
        {
            Token = token,
            Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
            NewPassword = newPassword
        };

        await auth.ChangeMasterPasswordAsync(request);

        var users = host.Services.GetRequiredService<IUserService>();
        var changedUser = await users.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("dave"));
        var merge = host.Services.GetRequiredService<IUserSnapshotMergeCoordinator>() as FakeUserSnapshotMergeCoordinator;
        var controlWriter = host.Services.GetRequiredService<IUserControlOperationWriterService>() as FakeUserControlOperationWriterService;
        var queueWriter = host.Services.GetRequiredService<ISyncQueueWriterService>() as FakeSyncQueueWriterService;
        MSTestAssert.IsNotNull(changedUser);
        MSTestAssert.AreEqual(2L, changedUser.KeyEpoch);
        MSTestAssert.AreEqual(1, merge?.Calls);
        MSTestAssert.AreEqual(1, controlWriter?.Calls);
        MSTestAssert.HasCount(1, queueWriter?.EnqueuedItems ?? []);

        var login = new LoginRequest
        {
            Username = "dave",
            Password = newPassword,
            RememberMe = false
        };

        var newToken = await auth.LoginAsync(login);

        MSTestAssert.AreNotEqual(Guid.Empty, newToken);
        MSTestAssert.AreNotEqual(token, newToken);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ChangeMasterPassword_InvalidatesOtherSessions()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var keys = host.Services.GetRequiredService<IKeyVaultService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var currentToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("multi_session"));
        var otherToken = await auth.LoginAsync(host.CreateValidLoginRequest("multi_session"));

        await auth.ChangeMasterPasswordAsync(new MasterPasswordChangeRequest
        {
            Token = currentToken,
            Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
            NewPassword = Encoding.UTF8.GetBytes("N3wP@ssw0rd_123456")
        });

        MSTestAssert.IsTrue(tokens.Validate(currentToken));
        MSTestAssert.IsTrue(keys.HasUserKey(currentToken));
        MSTestAssert.IsFalse(tokens.Validate(otherToken));
        MSTestAssert.IsFalse(keys.HasUserKey(otherToken));
        MSTestAssert.IsFalse(cache.TryGetUserData(otherToken, out _));
        MSTestAssert.IsTrue(tokens.TryGetInvalidationReason(otherToken, out var reason));
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.ProfilePasswordChanged, reason);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    [TestCategory("Security")]
    public async Task ChangeMasterPassword_CurrentEpochQuarantine_BlocksBeforeRotation()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var snapshots = host.Services.GetRequiredService<IUserSyncSnapshotRepository>() as FakeUserSyncSnapshotRepository;
        var controlWriter = host.Services.GetRequiredService<IUserControlOperationWriterService>() as FakeUserControlOperationWriterService;
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("quarantined_rotation"));
        var user = await users.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("quarantined_rotation"));
        MSTestAssert.IsNotNull(user);
        MSTestAssert.IsNotNull(snapshots);
        await snapshots.AddAsync(new UserSyncSnapshot
        {
            UserId = user.UId,
            OriginDeviceId = Guid.NewGuid(),
            OriginInstanceId = Guid.NewGuid(),
            OriginRevision = 1,
            UserKeyEpoch = user.KeyEpoch,
            MembershipEpoch = user.MembershipEpoch,
            SnapshotHash = new byte[32],
            OriginSignPublicKey = new byte[32],
            OriginSignature = new byte[64],
            EnvelopePayload = [0x01],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            Status = UserSyncSnapshotStatus.Quarantined
        });

        await ExpectThrowsAsync<InvalidOperationException>(() => auth.ChangeMasterPasswordAsync(new MasterPasswordChangeRequest
        {
            Token = token,
            Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
            NewPassword = Encoding.UTF8.GetBytes("N3wP@ssw0rd_123456")
        }));

        var unchanged = await users.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("quarantined_rotation"));
        MSTestAssert.IsNotNull(unchanged);
        MSTestAssert.AreEqual(1L, unchanged.KeyEpoch);
        MSTestAssert.AreEqual(0, controlWriter?.Calls);
        MSTestAssert.IsTrue(tokens.Validate(token));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    [TestCategory("Security")]
    public async Task ChangeMasterPassword_RememberMeEnabled_RewritesSavedKeyForNewEpoch()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var registration = host.CreateValidRegistrationRequest("remembered_rotation");
        registration.RememberMe = true;
        var token = await auth.RegisterAsync(registration);
        var before = await users.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("remembered_rotation"));
        MSTestAssert.IsNotNull(before);
        MSTestAssert.IsNotNull(before.SavedKey);
        var oldSavedKey = before.SavedKey.ToArray();

        await auth.ChangeMasterPasswordAsync(new MasterPasswordChangeRequest
        {
            Token = token,
            Password = Encoding.UTF8.GetBytes("P@ssw0rd12345678"),
            NewPassword = Encoding.UTF8.GetBytes("N3wP@ssw0rd_123456")
        });

        var after = await users.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("remembered_rotation"));
        MSTestAssert.IsNotNull(after);
        MSTestAssert.IsNotNull(after.SavedKey);
        MSTestAssert.AreEqual(2L, after.KeyEpoch);
        MSTestAssert.IsFalse(oldSavedKey.SequenceEqual(after.SavedKey));
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ChangeMasterPassword_WrongCurrentPassword_Throws()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("eve"));

        var request = new MasterPasswordChangeRequest
        {
            Token = token,
            Password = Encoding.UTF8.GetBytes("WRONG_PASSWORD"),
            NewPassword = Encoding.UTF8.GetBytes("AnotherValidPassword123")
        };

        await ExpectThrowsAsync<InvalidInputException>(async () =>
        {
            await auth.ChangeMasterPasswordAsync(request);
        });
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task Register_RememberMeEnabled_SavesKey()
    {
        using var host = new BackendTestHost();

        var auth = (IAuthService)host.Services.GetRequiredService(typeof(IAuthService));
        var users = (IUserService)host.Services.GetRequiredService(typeof(IUserService));

        var reg = host.CreateValidRegistrationRequest("remember_me_user");
        reg.RememberMe = true;

        await auth.RegisterAsync(reg);

        var user = await users.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("remember_me_user"));
        MSTestAssert.IsNotNull(user);
        MSTestAssert.IsNotNull(user.SavedKey);
        MSTestAssert.IsNotEmpty(user.SavedKey);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task RenewSession_MigratesKeyAndCache_AndRevokesOldToken()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var keys = host.Services.GetRequiredService<IKeyVaultService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();
        var oldToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("renew_user"));
        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(oldToken, out var oldUserData));

        var newToken = await auth.RenewSessionAsync(oldToken);

        MSTestAssert.AreNotEqual(oldToken, newToken);
        MSTestAssert.IsFalse(tokens.Validate(oldToken));
        MSTestAssert.IsFalse(keys.HasUserKey(oldToken));
        MSTestAssert.IsFalse(cache.TryGetUserDataBundle(oldToken, out _));
        MSTestAssert.IsTrue(tokens.TryGetInvalidationReason(oldToken, out var oldReason));
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.LoggedOut, oldReason);
        MSTestAssert.IsTrue(tokens.Validate(newToken));
        MSTestAssert.IsTrue(keys.HasUserKey(newToken));
        MSTestAssert.IsTrue(cache.TryGetUserDataBundle(newToken, out var newUserData));
        MSTestAssert.AreSame(oldUserData, newUserData);
        MSTestAssert.IsTrue(auth.GetSessionStatus(newToken).IsAuthenticated);
        MSTestAssert.IsFalse(auth.GetSessionStatus(oldToken).IsAuthenticated);
    }

    private static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            MSTestAssert.Fail($"Expected exception: {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}