using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserLookupServiceTests
{
    [TestMethod]
    public async Task GetAndVerifyUserByUid_ExistingUser_Works()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("alice"));
        var uid = sessions.GetUidFromToken(token);

        var fetched = await lookup.GetAndVerifyUserByUidAsync(uid);

        MSTestAssert.AreEqual(uid, fetched.UId);
    }

    [TestMethod]
    public async Task GetAndVerifyUserByUid_NonExisting_Throws()
    {
        using var host = new BackendTestHost();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();

        await MSTestAssert.ThrowsExactlyAsync<UserNotFoundException>(
            () => lookup.GetAndVerifyUserByUidAsync(Guid.NewGuid()));
    }

    [TestMethod]
    public async Task GetUserByUsername_UsesLightweightLookupInsteadOfListingFullUsers()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();
        var repository = (InMemoryUserRepository)host.Services.GetRequiredService<IUserRepository>();

        await auth.RegisterAsync(host.CreateValidRegistrationRequest("lookup_user"));
        var loginLookupCallsBefore = repository.LoginLookupCallCount;
        var listAllCallsBefore = repository.ListAllCallCount;

        var user = await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("lookup_user"));

        MSTestAssert.IsNotNull(user);
        MSTestAssert.IsTrue(repository.LoginLookupCallCount > loginLookupCallsBefore);
        MSTestAssert.AreEqual(listAllCallsBefore, repository.ListAllCallCount);
    }

    [TestMethod]
    public async Task GetUserByUsername_NonExisting_ReturnsNull()
    {
        using var host = new BackendTestHost();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();

        var user = await lookup.GetUserByUsernameAsync(Encoding.UTF8.GetBytes("nobody"));

        MSTestAssert.IsNull(user);
    }

    [TestMethod]
    public async Task GetAndVerifyRememberMeEnabledUsers_ReturnsOnlyEnabled()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();

        await auth.RegisterAsync(host.CreateValidRegistrationRequest("steve"));
        var rememberMeUser = host.CreateValidRegistrationRequest("alice_remembered");
        rememberMeUser.RememberMe = true;
        await auth.RegisterAsync(rememberMeUser);

        var users = await lookup.GetAndVerifyRememberMeEnabledUsersAsync();

        MSTestAssert.HasCount(1, users);
    }
}
