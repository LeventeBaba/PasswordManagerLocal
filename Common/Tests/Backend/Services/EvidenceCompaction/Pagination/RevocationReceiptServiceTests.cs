using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Repositories;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RevocationReceiptServiceTests
{
    [TestMethod]
    [TestCategory("EvidenceCompaction")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EvidenceCompaction_KeysetContinuationPassesBlockedAndOversizedPrefixes(bool oversized)
    {
        await using var scenario = await Scenario.CreateAsync(peerCount: 2, authenticatedHistory: true);
        scenario.Operation.OperationId = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
        UserControlOperationEnvelopeUtil.FillOriginAuthentication(scenario.Operation, scenario.Peers[0]);
        await scenario.Inbox.StoreAndApplyAsync(scenario.Operation, scenario.Peers[0].LocalDeviceId);
        var source = (await scenario.Db.RevocationReceipts.GetEvidenceAsync(scenario.Operation.OperationId))!;
        Guid reclaimableId = default;
        await using (var transaction = await scenario.Db.UnitOfWork.BeginTransactionAsync())
        {
            for (var index = 1; index <= 9; index++)
            {
                var operation = UserControlOperationEnvelopeUtil.CopyCoreProof(scenario.Operation);
                operation.OperationId = Guid.Parse("00000000-0000-0000-0000-" + index.ToString("D12"));
                operation.OriginSequence += index;
                operation.RevocationHistoryProofs.AddRange(scenario.Operation.RevocationHistoryProofs);
                UserControlOperationEnvelopeUtil.FillOriginAuthentication(operation, scenario.Peers[0]);
                var payload = UserControlOperationEnvelopeUtil.Serialize(operation);
                if (oversized && index == 1)
                {
                    var prefix = Encoding.UTF8.GetString(payload)[..^1];
                    payload = Encoding.UTF8.GetBytes(prefix + ",\"Padding\":\"" + new string('x', 17 * 1024 * 1024) + "\"}");
                }
                await scenario.Db.RevocationReceipts.AddEvidenceAsync(new RevocationReceiptEvidence
                {
                    OperationId = operation.OperationId, UserId = operation.UserId, OperationHash = operation.OperationHash,
                    EnvelopePayload = payload, AuthorizationPayload = source.AuthorizationPayload,
                    MembershipProofPayload = source.MembershipProofPayload, HistoryResetBlocked = index < 9 && !(oversized && index == 1)
                });
                if (index == 9) reclaimableId = operation.OperationId;
            }
            await scenario.Db.UnitOfWork.SaveChangesAsync(); await transaction.CommitAsync();
        }
        using var session = scenario.Db.OpenSession();
        var compactor = new SqliteSynchronizationEvidenceCompactor(session, scenario.Lifecycle);
        var first = await compactor.CompactBatchAsync();
        Assert.AreEqual(0L, first.ReclaimedBytes);
        Assert.IsNotNull(first.Next);
        Assert.AreEqual("Revocation", first.Next.Category);
        var second = await compactor.CompactBatchAsync(first.Next);
        Assert.IsTrue(second.ReclaimedBytes > 0);
        var retained = (await new RevocationReceiptRepository(session).GetEvidenceAsync(reclaimableId))!;
        Assert.AreEqual(0, UserControlOperationEnvelopeUtil.Deserialize(retained.EnvelopePayload).RevocationHistoryProofs.Count);
        CollectionAssert.AreEqual(source.MembershipProofPayload, retained.MembershipProofPayload);
        Assert.IsNotNull(second.Next); Assert.AreEqual("Faults", second.Next.Category);
        var complete = await compactor.CompactBatchAsync(second.Next);
        Assert.IsNull(complete.Next);
        Assert.IsNotNull(await scenario.Db.DeletedUserBarriers.GetAsync(scenario.User.UId));
    }
}
