using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserDataRecoveryCoordinator : IUserDataRecoveryCoordinator
{
    public List<FakeUserDataRecoveryCall> Calls { get; } = [];
    public UserDataRecoveryResult Result { get; set; } =
        new(UserDataRecoveryState.NothingToRecover, DiagnosticCode: "fake-no-recovery-needed");
    public byte[]? PasswordSalt { get; set; }

    public Task<UserDataRecoveryResult> TryRecoverAsync(
        Guid userId,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        UserDataRecoveryTrigger trigger,
        CancellationToken ct = default)
    {
        Calls.Add(new FakeUserDataRecoveryCall(userId, keyConfidence, trigger));
        return Task.FromResult(Result);
    }

    public Task<byte[]?> TryResolvePasswordSaltAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(PasswordSalt?.ToArray());

}
