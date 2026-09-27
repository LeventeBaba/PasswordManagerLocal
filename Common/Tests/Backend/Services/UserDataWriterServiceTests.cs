using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDataWriterServiceTests
{
    [TestMethod]
    public async Task UpdateUserDataBundle_ChangesPersist()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var writer = host.Services.GetRequiredService<IUserDataWriterService>();
        var cache = host.Services.GetRequiredService<IDataCachingService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("writer_user"));
        var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
        bundle.GeneralUserData.FirstName = "Updated";

        await writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.General);

        cache.InvalidateToken(token);
        var reloaded = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
        MSTestAssert.AreEqual("Updated", reloaded.GeneralUserData.FirstName);
    }

    [TestMethod]
    public async Task UpdateUserDataBundle_ChangedPasswordWithoutUpdatedChildHash_Throws()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var reader = host.Services.GetRequiredService<IUserDataReaderService>();
        var writer = host.Services.GetRequiredService<IUserDataWriterService>();
        var passwords = host.Services.GetRequiredService<IPasswordService>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("integrity_guard"));
        var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
        await passwords.AddNewPassword(new NewPasswordRequest
        {
            Name = "Email",
            Password = Encoding.UTF8.GetBytes("SecretPassword123")
        }, bundle.UserPasswordsData);
        await writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords);

        bundle.UserPasswordsData.Passwords[0].Name = "Changed without regenerating integrity";

        await MSTestAssert.ThrowsExactlyAsync<InvalidDataIntegrityException>(
            () => writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Passwords));
    }
}
