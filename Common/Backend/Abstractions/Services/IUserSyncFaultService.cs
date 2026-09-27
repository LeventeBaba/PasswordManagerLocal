using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSyncFaultService
{
    Task<UserSyncFault> RecordAsync(UserSyncFaultDescriptor descriptor, CancellationToken ct = default);
    Task MarkOriginRecoveredAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long keyEpoch, long supersedingRevision, CancellationToken ct = default);
    Task MarkLocalCanonicalRecoveredAsync(Guid userId, CancellationToken ct = default);
    Task<bool> IsPublishingBlockedAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasTerminalOriginFaultAsync(Guid userId, Guid originDeviceId, Guid originInstanceId, long keyEpoch, CancellationToken ct = default);
}
