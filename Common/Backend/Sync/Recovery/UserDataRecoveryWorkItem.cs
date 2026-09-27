using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PasswordManagerLocal.Common.Backend.Sync.Recovery;

internal sealed record UserDataRecoveryWorkItem(
    Guid UserId,
    UserDataRecoveryTrigger Trigger);
