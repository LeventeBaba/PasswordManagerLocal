using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Sync.Tombstones;
using PasswordManagerLocal.Common.Backend.Utils;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Backend.Sync.Tombstones;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Tombstones;

[TestClass]
public sealed class TombstoneCleanupUtilTests
{
    [TestMethod]
    public void AddOrUpdateDeletedPassword_DoesNotDiscardExistingTombstones()
    {
        var now = DateTime.UtcNow;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var passwords = new UserPasswordsData();

        TombstoneCleanupUtil.AddOrUpdateDeletedPassword(passwords, firstId, now.AddYears(-5), Stamp(now.AddYears(-5)));
        TombstoneCleanupUtil.AddOrUpdateDeletedPassword(passwords, secondId, now, Stamp(now, 1));

        MSTestAssert.HasCount(2, passwords.DeletedPasswords);
        MSTestAssert.IsTrue(passwords.DeletedPasswords.Any(item => item.Id == firstId));
        MSTestAssert.IsTrue(passwords.DeletedPasswords.Any(item => item.Id == secondId));
    }

    [TestMethod]
    public void AddOrUpdateDeletedPassword_NewLogicalDeletionClearsPreviousCausalAnchor()
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var passwords = new UserPasswordsData();
        TombstoneCleanupUtil.AddOrUpdateDeletedPassword(passwords, id, now.AddDays(-1), Stamp(now.AddDays(-1)));
        passwords.DeletedPasswords[0].CausalReference = Reference(3);

        TombstoneCleanupUtil.AddOrUpdateDeletedPassword(passwords, id, now, Stamp(now, 2));

        MSTestAssert.IsFalse(passwords.DeletedPasswords[0].CausalReference.IsValid);
        MSTestAssert.AreEqual(2, passwords.DeletedPasswords[0].Version.LogicalCounter);
    }

    private static TombstoneCausalReference Reference(long revision) => new()
    {
        OriginDeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        OriginInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        UserKeyEpoch = 1,
        MembershipEpoch = 1,
        OriginRevision = revision
    };

    private static SyncVersionStamp Stamp(DateTime value, long logical = 0) => new()
    {
        PhysicalTimeUnixMilliseconds = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        LogicalCounter = logical,
        OriginDeviceId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        OriginInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };
}
