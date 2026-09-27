using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSessionServiceTests
{
    [TestMethod]
    public async Task GetUidFromToken_ValidToken_ReturnsRegisteredUser()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("session_user"));

        MSTestAssert.AreNotEqual(Guid.Empty, sessions.GetUidFromToken(token));
    }

    [TestMethod]
    public void GetUidFromToken_InvalidToken_Throws()
    {
        using var host = new BackendTestHost();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();

        MSTestAssert.ThrowsExactly<InvalidTokenException>(() => sessions.GetUidFromToken(Guid.NewGuid()));
    }

    [TestMethod]
    public void GetEncryptionKeyFromToken_InvalidToken_Throws()
    {
        using var host = new BackendTestHost();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();

        MSTestAssert.ThrowsExactly<InvalidTokenException>(() => sessions.GetEncryptionKeyFromToken(Guid.NewGuid()));
    }
}
