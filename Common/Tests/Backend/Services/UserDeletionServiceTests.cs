using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDeletionServiceTests
{
    [TestMethod]
    public async Task DeleteUserByToken_CreatesSignedBarrier_RemovesUser_AndInvalidatesSession()
    {
        using var host = new BackendTestHost();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var sessions = host.Services.GetRequiredService<IUserSessionService>();
        var lookup = host.Services.GetRequiredService<IUserLookupService>();
        var deletion = host.Services.GetRequiredService<IUserDeletionService>();
        var barriers = host.Services.GetRequiredService<IDeletedUserBarrierRepository>();
        var operations = host.Services.GetRequiredService<IUserControlOperationRepository>();

        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest("deleted_user"));
        var uid = sessions.GetUidFromToken(token);

        await deletion.DeleteUserByTokenAsync(token);

        MSTestAssert.IsFalse(await lookup.UserExistsAsync(uid));
        var barrier = await barriers.GetAsync(uid);
        MSTestAssert.IsNotNull(barrier);
        var deletionOperation = (await operations.ListForUserAsync(uid)).Single(operation =>
            operation.OperationType == UserControlOperationType.AccountDeletion);
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, deletionOperation.Status);
        MSTestAssert.AreEqual(deletionOperation.OperationId, barrier.DeletionOperationId);
        MSTestAssert.IsTrue(deletionOperation.OperationHash.SequenceEqual(barrier.OperationHash));

        var session = auth.GetSessionStatus(token);
        MSTestAssert.IsFalse(session.IsAuthenticated);
        MSTestAssert.AreEqual(AuthSessionInvalidationReason.ProfileRemoved, session.InvalidationReason);
    }
}
