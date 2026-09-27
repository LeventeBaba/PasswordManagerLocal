using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class RejectingCanonicalHealthService : IUserCanonicalHealthService
{
    public int VerifyCalls { get; private set; }
    public int UpdateCheckpointCalls { get; private set; }

    public Task<CanonicalHealthResult> VerifyAsync(
        User user,
        EncryptionKey? key,
        UserSyncKeyConfidence keyConfidence,
        bool recordFault,
        CancellationToken ct = default)
    {
        VerifyCalls++;
        return Task.FromResult(new CanonicalHealthResult(
            UserDataVerificationState.CheckpointFailure,
            UserDataBlobKind.All,
            keyConfidence,
            "canonical-checkpoint-mismatch")
        {
            RowIntegrityVerified = true
        });
    }

    public Task UpdateCheckpointAsync(User user, CancellationToken ct = default)
    {
        UpdateCheckpointCalls++;
        return Task.CompletedTask;
    }

    public Task DeleteCheckpointAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
