using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Recovery")]
[TestCategory("TransactionHardening")]
public sealed class UserCanonicalRecoveryStoreTests
{
    [TestMethod]
    public async Task CaptureMissingAccount_CompletesEveryQueryAndReleasesItsReadSnapshot()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var store = new UserCanonicalRecoveryStore(database.UnitOfWork);
        // Call the store directly: SQL/provider failures must surface as their original
        // exceptions instead of the coordinator's deliberately fail-closed Failed result.
        using var input = await store.CaptureAsync(Guid.NewGuid());
        Assert.IsNull(input.User);
        Assert.IsFalse(input.PendingControl);
        Assert.IsFalse(input.Deleted);
        Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
        Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
        await using var transaction = await database.UnitOfWork.BeginTransactionAsync();
        Assert.IsTrue(await store.IsCurrentAsync(input));
        await transaction.RollbackAsync();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(65_535)]
    [DataRow(65_536)]
    [DataRow(65_537)]
    [DataRow(131_075)]
    public async Task CaptureAndRevalidation_PreserveEmptyNullAndMultiChunkBlobValues(int payloadBytes)
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var payload = Enumerable.Range(0, payloadBytes).Select(index => (byte)(index % 251)).ToArray();
        var user = new User
        {
            KeyEpoch = 1, MembershipEpoch = 1, EncryptedPayload = payload,
            EncryptedGeneralUserDataPayload = [], EncryptedUserPasswordsDataPayload = [0x11, 0x22],
            EncryptedUserDevicesDataPayload = [0x33], UsernameHash = [0x44], UsernameSalt = [0x55],
            PasswordSalt = [0x66], SavedKey = payloadBytes % 2 == 0 ? null : []
        };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        var store = new UserCanonicalRecoveryStore(database.UnitOfWork);
        using var input = await store.CaptureAsync(user.UId);
        Assert.IsNotNull(input.User);
        CollectionAssert.AreEqual(payload, input.User.EncryptedPayload);
        Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
        Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
        using var repeated = await store.CaptureAsync(user.UId);
        CollectionAssert.AreEqual(input.Commitment, repeated.Commitment);
        await using (var transaction = await database.UnitOfWork.BeginTransactionAsync())
        {
            Assert.IsTrue(await store.IsCurrentAsync(input));
            await transaction.RollbackAsync();
        }
        // Hashing and disposing temporary BLOB reads must not clear committed data.
        var persisted = (await database.Users.GetByIdAsNoTrackingAsync(user.UId))!;
        CollectionAssert.AreEqual(payload, persisted.EncryptedPayload);
        CollectionAssert.AreEqual(new byte[] { 0x11, 0x22 }, persisted.EncryptedUserPasswordsDataPayload);
    }

    [TestMethod]
    public async Task PendingCriticalIngress_IsCapturedAsAnAccessFence()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        await database.UnitOfWork.ExecuteFixtureSqlAsync(
            "INSERT INTO CriticalControlIngress VALUES (zeroblob(32), $user, $operation, zeroblob(32), $peer, x'01');",
            ("$user", userId), ("$operation", Guid.NewGuid()), ("$peer", Guid.NewGuid()));
        using var input = await new UserCanonicalRecoveryStore(database.UnitOfWork).CaptureAsync(userId);
        Assert.IsTrue(input.PendingControl);
        Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
    }

    [TestMethod]
    public async Task ChangedCiphertextWithUnchangedIntegrityHashAndTimestamps_IsRejected()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var user = new User { KeyEpoch = 1, MembershipEpoch = 1, EncryptedPayload = [0x11, 0x22, 0x33] };
        user.GenerateIntegrityHash();
        await database.Users.AddAsync(user);
        await database.UnitOfWork.SaveChangesAsync();
        var store = new UserCanonicalRecoveryStore(database.UnitOfWork);
        using var input = await store.CaptureAsync(user.UId);
        using (var writer = database.OpenSession())
            await writer.ExecuteFixtureSqlAsync("UPDATE Users SET EncryptedPayload = x'112244' WHERE UId = $user;", ("$user", user.UId));
        await using var transaction = await database.UnitOfWork.BeginTransactionAsync();
        Assert.IsFalse(await store.IsCurrentAsync(input));
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
        await transaction.RollbackAsync();
        var current = (await database.Users.GetByIdAsNoTrackingAsync(user.UId))!;
        CollectionAssert.AreEqual(user.IntegrityHash, current.IntegrityHash);
        Assert.AreEqual(user.LastModifiedAt.ToUnixTimeMilliseconds(), current.LastModifiedAt.ToUnixTimeMilliseconds());
        CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x44 }, current.EncryptedPayload);
    }

    [TestMethod]
    public async Task AlreadyCancelledCapture_PropagatesCancellationAndLeavesTheSessionReusable()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var store = new UserCanonicalRecoveryStore(database.UnitOfWork);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.CaptureAsync(Guid.NewGuid(), cancellation.Token));
        Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
        Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
        using var input = await store.CaptureAsync(Guid.NewGuid());
        Assert.IsNull(input.User);
    }

    [TestMethod]
    public async Task ExistingReadSnapshot_CannotBeInheritedByRecoveryPreparation()
    {
        await using var database = await SqliteRepositoryTestDatabase.CreateAsync();
        var store = new UserCanonicalRecoveryStore(database.UnitOfWork);
        await database.UnitOfWork.ReadSnapshotAsync(async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CaptureAsync(Guid.NewGuid()));
            Assert.IsTrue(database.UnitOfWork.HasActiveReadSnapshot);
            return true;
        }, CancellationToken.None);
        Assert.IsFalse(database.UnitOfWork.HasActiveReadSnapshot);
        Assert.IsFalse(database.UnitOfWork.HasActiveTransaction);
        Assert.IsFalse(database.UnitOfWork.HasPendingChanges);
    }
}
