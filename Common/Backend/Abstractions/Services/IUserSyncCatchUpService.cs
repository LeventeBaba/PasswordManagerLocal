namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserSyncCatchUpService
{
    Task EnqueueAsync(Guid userId, Guid targetDeviceId, CancellationToken ct = default);
}
