using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed class FakeUserDataRecoveryScheduler : IUserDataRecoveryScheduler
{
    public List<(Guid UserId, UserDataRecoveryTrigger Trigger)> Calls { get; } = [];

    public void Schedule(Guid userId, UserDataRecoveryTrigger trigger) =>
        Calls.Add((userId, trigger));
}
