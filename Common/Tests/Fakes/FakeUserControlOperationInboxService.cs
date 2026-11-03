using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserControlOperationInboxService : IUserControlOperationInboxService
{
    public List<Guid> ApplyCalls { get; } = [];
    public Func<Guid, CancellationToken, Task<UserControlOperationReceiptResult>>? ApplyHandler { get; set; }

    public Task<UserControlOperationReceiptResult> StoreAndApplyAsync(
        UserControlOperationEnvelope envelope,
        Guid transportPeerDeviceId,
        CancellationToken ct = default) =>
        Task.FromResult(Create(envelope));

    public Task<UserControlOperationReceiptResult> TryApplyStoredAsync(Guid operationId, CancellationToken ct = default)
    {
        ApplyCalls.Add(operationId);
        return ApplyHandler?.Invoke(operationId, ct) ?? Task.FromResult(new UserControlOperationReceiptResult(
            operationId, Guid.Empty, Guid.Empty, Guid.Empty, 0, [], UserControlOperationReceiptState.StoredPending));
    }

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
