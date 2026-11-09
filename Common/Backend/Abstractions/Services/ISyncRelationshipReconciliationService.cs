using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface ISyncRelationshipReconciliationService
{
    Task SyncUserDevicesAsync(User user, IEnumerable<Guid> deviceIds, DateTimeOffset modifiedAt, CancellationToken ct = default);
    Task SyncDeviceUsersAsync(Device device, IEnumerable<Guid> userIds, DateTimeOffset modifiedAt, CancellationToken ct = default);
}
