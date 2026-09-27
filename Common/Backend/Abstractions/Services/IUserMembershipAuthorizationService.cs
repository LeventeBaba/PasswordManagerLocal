using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserMembershipAuthorizationService
{
    Task<UserMembershipAuthorization> CreateGenesisAsync(User user, CancellationToken ct = default);
    Task<UserMembershipAuthorization> AuthorizeAdditionAsync(DeviceAdditionPayload payload, Guid operationId, byte[] operationHash, CancellationToken ct = default);
    Task EndAuthorizationAsync(UserMembershipAuthorization authorization, DeviceRemovalPayload payload, Guid operationId, byte[] operationHash, CancellationToken ct = default);
    Task<UserMembershipAuthorization> VerifySnapshotAuthorAsync(UserSnapshotEnvelope envelope, CancellationToken ct = default);
    Task<UserMembershipAuthorization> VerifyControlAuthorAsync(UserControlOperationEnvelope envelope, CancellationToken ct = default);
}
