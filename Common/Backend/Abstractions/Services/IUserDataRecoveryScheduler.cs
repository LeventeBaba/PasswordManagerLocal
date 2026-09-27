using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Backend.Abstractions.Services;

public interface IUserDataRecoveryScheduler
{
    void Schedule(Guid userId, UserDataRecoveryTrigger trigger);
}
