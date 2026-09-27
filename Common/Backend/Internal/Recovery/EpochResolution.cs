using Microsoft.EntityFrameworkCore;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

internal sealed record EpochResolution(
    bool Success,
    long KeyEpoch,
    long MembershipEpoch,
    UserDataRecoveryState State,
    string? DiagnosticCode)
{
    public static EpochResolution Failed(UserDataRecoveryState state, string diagnosticCode) =>
        new(false, 0, 0, state, diagnosticCode);
}
