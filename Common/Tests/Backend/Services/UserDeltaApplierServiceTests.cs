using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserDeltaApplierServiceTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task ApplyAsync_OrdinaryUserUpdate_RejectsBlindCanonicalReplacement()
    {
        var service = new UserDeltaApplierService(
            new InMemoryUserRepository(),
            new FakeSyncTombstoneRepository(),
            new FakeSyncQueueService());
        var payload = new SyncDeltaPayload
        {
            ModelId = Guid.NewGuid(),
            ModelType = SyncModelType.User,
            ChangeType = SyncChangeType.Updated,
            UserSnapshot = new UserSnapshotEnvelope()
        };

        await MSTestAssert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.ApplyAsync(payload, Guid.NewGuid(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), CancellationToken.None));
    }
}
