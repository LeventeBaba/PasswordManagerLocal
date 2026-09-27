using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Security.Cryptography;
using System.Text.Json;

namespace PasswordManagerLocal.Common.Backend.Internal.Identity;

internal sealed record UserLoginIdentityCandidate(
    Guid UserId,
    byte[] UsernameHash,
    byte[] UsernameSalt,
    SyncVersionStamp Version,
    Guid SourceOriginDeviceId,
    Guid SourceOriginInstanceId,
    long SourceOriginRevision,
    byte[] SourceSnapshotHash,
    long KeyEpoch,
    long MembershipEpoch)
{
    public static UserLoginIdentityCandidate FromCanonical(User user, SyncVersionStamp version) => new(
        user.UId,
        user.UsernameHash,
        user.UsernameSalt,
        version,
        version.OriginDeviceId,
        version.OriginInstanceId,
        0,
        [],
        user.KeyEpoch,
        user.MembershipEpoch);

    public static UserLoginIdentityCandidate FromSnapshot(UserSnapshotEnvelope envelope) => new(
        envelope.UserId,
        envelope.User.UsernameHash,
        envelope.User.UsernameSalt,
        envelope.User.GeneralUserDataVersion,
        envelope.OriginDeviceId,
        envelope.OriginInstanceId,
        envelope.OriginRevision,
        envelope.SnapshotHash,
        envelope.UserKeyEpoch,
        envelope.MembershipEpoch);
}
