using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncRelationshipReconciliationService
{
    Task SyncDeviceUsersAsync(Device device, IEnumerable<Guid> userIds, DateTimeOffset modifiedAt, CancellationToken ct = default);
}
