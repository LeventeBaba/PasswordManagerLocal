using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("MigrationStorage")]
[TestCategory("StoragePhysical")]
public sealed class DatabaseMigrationStoragePolicyTests
{
    [TestMethod]
    public void WorkspaceAdmission_RequiresItsCompleteAllowance_AndRejectsUnknownSpace()
    {
        const long sourceBytes = 8192;
        var directory = Path.GetTempPath();
        var required = new DatabaseMigrationStoragePolicy().RequiredWorkingBytes(sourceBytes);
        Assert.IsTrue(required >= sourceBytes * 4 + StorageCapacityConstants.MigrationWorkspaceReserveBytes);
        new DatabaseMigrationStoragePolicy(availableSpace: path =>
        {
            Assert.AreEqual(directory, path);
            return required;
        }).RequireWorkingSpace(directory, sourceBytes);
        Assert.ThrowsExactly<StorageCapacityExceededException>(() =>
            new DatabaseMigrationStoragePolicy(availableSpace: _ => required - 1).RequireWorkingSpace(directory, sourceBytes));
        var failure = Assert.ThrowsExactly<StorageCapacityExceededException>(() =>
            new DatabaseMigrationStoragePolicy(availableSpace: _ => throw new IOException("unavailable"))
                .RequireWorkingSpace(directory, sourceBytes));
        Assert.IsInstanceOfType<IOException>(failure.InnerException);
    }

    [TestMethod]
    public void InvalidLimitsAndSourceImages_CannotBypassTheProductionCeiling()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DatabaseMigrationStoragePolicy(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new DatabaseMigrationStoragePolicy(StorageCapacityConstants.MainDatabaseBytes + 1));
        var policy = new DatabaseMigrationStoragePolicy(1024);
        Assert.ThrowsExactly<StorageCapacityExceededException>(() => policy.RequiredWorkingBytes(1025));
        Assert.ThrowsExactly<StorageCapacityExceededException>(() => policy.RequiredWorkingBytes(-1));
    }
}
