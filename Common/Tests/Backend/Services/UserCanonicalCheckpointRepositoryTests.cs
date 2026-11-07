using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories.Sqlite;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserCanonicalCheckpointRepositoryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task GetAsync_ReturnsAddedCheckpointBeforeSaveChanges(bool sqlite)
    {
        await using var database = await SqliteIntegrationTestDatabase.CreateSharedAsync();
        using var session = new SqliteSession(new SqliteConnectionFactory(database.ConnectionString));
        IUserCanonicalCheckpointRepository repository = sqlite
            ? new SqliteUserCanonicalCheckpointRepository(session)
            : database.UserCanonicalCheckpoints;
        var checkpoint = new UserCanonicalCheckpoint
        {
            UserId = Guid.NewGuid(),
            CheckpointSequence = 1,
            LocalDeviceId = Guid.NewGuid(),
            LocalOriginInstanceId = Guid.NewGuid(),
            KeyEpoch = 1,
            MembershipEpoch = 1,
            CanonicalContentHash = new byte[32],
            UserIntegrityHash = new byte[32],
            SignPublicKey = new byte[32],
            Signature = new byte[64]
        };

        await repository.AddAsync(checkpoint);

        var loaded = await repository.GetAsync(checkpoint.UserId);

        Assert.AreSame(checkpoint, loaded);
        Assert.AreEqual(0, await database.Db.UserCanonicalCheckpoints.CountAsync());
    }
}
