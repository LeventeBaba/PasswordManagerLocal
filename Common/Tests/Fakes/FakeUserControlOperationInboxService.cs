using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserControlOperationInboxService : IUserControlOperationInboxService
{
    public Task<UserControlOperationReceiptResult> StoreAndApplyAsync(
        UserControlOperationEnvelope envelope,
        Guid transportPeerDeviceId,
        CancellationToken ct = default) =>
        Task.FromResult(Create(envelope));

    public Task<UserControlOperationReceiptResult> TryApplyStoredAsync(Guid operationId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    private static UserControlOperationReceiptResult Create(UserControlOperationEnvelope envelope) =>
        new(
            envelope.OperationId,
            envelope.UserId,
            envelope.OriginDeviceId,
            envelope.OriginInstanceId,
            envelope.OriginSequence,
            envelope.OperationHash.ToArray(),
            UserControlOperationReceiptState.StoredPending);
}
