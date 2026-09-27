using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Tests.Fakes;

public sealed record FakeUserDataRecoveryCall(
    Guid UserId,
    UserSyncKeyConfidence KeyConfidence,
    UserDataRecoveryTrigger Trigger);
