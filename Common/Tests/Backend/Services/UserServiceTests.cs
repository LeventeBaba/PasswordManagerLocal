using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserServiceTests
{
    [TestMethod]
    public async Task Facade_DelegatesAcrossFocusedUserServices()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var users = host.Services.GetRequiredService<IUserService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("facade_user"));
        var uid = users.GetUidFromToken(token);
        var user = await users.GetAndVerifyUserByUidAsync(uid);
        var bundle = await users.GetLoadAndVerifyUserDataBundleAsync(token);

        MSTestAssert.AreEqual(uid, user.UId);
        MSTestAssert.AreEqual(uid, bundle.UserData.UId);
    }
}
