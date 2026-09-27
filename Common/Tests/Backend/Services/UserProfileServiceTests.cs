using global::PasswordManagerLocal.Common.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Requests;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserProfileServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task GetUserProfileInfo_AfterRegister_ReturnsExpected()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var profile = host.Services.GetRequiredService<IUserProfileService>();

        var reg = host.CreateValidRegistrationRequest("alice");
        reg.FirstName = "Alice";
        reg.LastName = "Liddell";
        reg.Email = "alice@example.com";

        var token = await auth.RegisterAsync(reg);

        var info = await profile.GetUserProfileInfoAsync(token);

        MSTestAssert.AreEqual("alice", info.Username);
        MSTestAssert.AreEqual("Alice", info.FirstName);
        MSTestAssert.AreEqual("Liddell", info.LastName);
        MSTestAssert.AreEqual("alice@example.com", info.Email);
        MSTestAssert.IsTrue(info.RegistrationDate > DateTime.MinValue);
        MSTestAssert.AreEqual(TimeZoneInfo.Local.Id, info.RegistrationTimeZoneId);
        MSTestAssert.AreEqual(DeviceType.WindowsPc, info.RegistrationDeviceType);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task UpdateUserProfileInfo_ChangesPersist()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var profile = host.Services.GetRequiredService<IUserProfileService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("bob"));

        await profile.UpdateUserProfileInfoAsync(new UpdateUserProfileRequest
        {
            Token = token,
            NewEamil = "bob.new@example.com",
            newFirstName = "Bobby",
            NewLastName = "Tables"
        });

        cache.InvalidateToken(token);
        var info = await profile.GetUserProfileInfoAsync(token);

        MSTestAssert.AreEqual("bob", info.Username);
        MSTestAssert.AreEqual("Bobby", info.FirstName);
        MSTestAssert.AreEqual("Tables", info.LastName);
        MSTestAssert.AreEqual("bob.new@example.com", info.Email);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ChangeUsername_OldLoginFails_NewLoginWorks_AndProfileUpdated()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var profile = host.Services.GetRequiredService<IUserProfileService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("carol"));

        await profile.ChangeUsernameAsync(token, "carol2");

        var info = await profile.GetUserProfileInfoAsync(token);
        MSTestAssert.AreEqual("carol2", info.Username);

        await ExpectThrowsAsync<UserNotFoundException>(async () =>
        {
            await auth.LoginAsync(host.CreateValidLoginRequest("carol"));
        });

        var token2 = await auth.LoginAsync(host.CreateValidLoginRequest("carol2"));
        MSTestAssert.AreNotEqual(Guid.Empty, token2);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ChangeUsername_DuplicateUsername_ThrowsAndKeepsOriginalUsername()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var profile = host.Services.GetRequiredService<IUserProfileService>();

        var aliceToken = await auth.RegisterAsync(host.CreateValidRegistrationRequest("alice_duplicate_test"));
        await auth.RegisterAsync(host.CreateValidRegistrationRequest("bob_duplicate_test"));

        await ExpectThrowsAsync<InvalidInputException>(async () =>
        {
            await profile.ChangeUsernameAsync(aliceToken, "bob_duplicate_test");
        });

        var info = await profile.GetUserProfileInfoAsync(aliceToken);
        MSTestAssert.AreEqual("alice_duplicate_test", info.Username);

        var loginToken = await auth.LoginAsync(host.CreateValidLoginRequest("alice_duplicate_test"));
        MSTestAssert.AreNotEqual(Guid.Empty, loginToken);
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task DeleteUserAccount_CorrectPassword_DeletesUser_AndInvalidatesAllSessions()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var profile = host.Services.GetRequiredService<IUserProfileService>();
        var users = host.Services.GetRequiredService<IUserService>();
        var tokens = host.Services.GetRequiredService<ITokenService>();
        var keys = host.Services.GetRequiredService<IKeyVaultService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("dave"));
        var secondToken = await auth.LoginAsync(host.CreateValidLoginRequest("dave"));
        var uid = users.GetUidFromToken(token);

        await profile.DeleteUserAccountAsync(token, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));

        var exists = await users.UserExistsAsync(uid);
        MSTestAssert.IsFalse(exists);

        MSTestAssert.IsFalse(tokens.Validate(token));
        MSTestAssert.IsFalse(keys.HasUserKey(token));
        MSTestAssert.IsFalse(tokens.Validate(secondToken));
        MSTestAssert.IsFalse(keys.HasUserKey(secondToken));
        MSTestAssert.IsTrue(tokens.TryGetInvalidationReason(secondToken, out var reason));
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.ProfileRemoved, reason);

        await ExpectThrowsAsync<InvalidTokenException>(async () =>
        {
            await profile.GetUserProfileInfoAsync(token);
        });
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task InvalidRequests_Throw()
    {
        using var host = new BackendTestHost();

        var auth = host.Services.GetRequiredService<IAuthService>();
        var profile = host.Services.GetRequiredService<IUserProfileService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("erin"));

        await ExpectThrowsAsync<InvalidInputException>(async () =>
        {
            await profile.UpdateUserProfileInfoAsync(new UpdateUserProfileRequest
            {
                Token = token
            });
        });

        await ExpectThrowsAsync<InvalidInputException>(async () =>
        {
            await profile.ChangeUsernameAsync(token, "");
        });

        await ExpectThrowsAsync<InvalidTokenException>(async () =>
        {
            await profile.GetUserProfileInfoAsync(Guid.NewGuid());
        });
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