namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserPostLoginWorkScheduler
{
    void DeferUntilInitialRead(Guid userId, long keyEpoch, long membershipEpoch, Guid token);
    void ReleaseInitialRead(Guid userId, Guid token);
    bool IsInitialReadPending(Guid userId);
    void Schedule(Guid userId);
    void CancelToken(Guid token);
}
