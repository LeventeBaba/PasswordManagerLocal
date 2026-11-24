using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Sync;

internal static class UserSnapshotEvidenceUtil
{
    // The row is an index, never an alternate source of signed identity or coverage.
    // Callers must then verify historical authorship, which also validates structure/hash.
    internal static UserSnapshotEnvelope DeserializeExact(UserSyncSnapshot row)
    {
        if (row.EnvelopePayload.Length == 0 || row.EnvelopePayload.Length > SyncConstants.MaxUserSnapshotEnvelopeBytes)
            throw new InvalidDataException("The retained snapshot envelope size is invalid.");
        var envelope = JsonSerializer.Deserialize(row.EnvelopePayload, BackendJsonSerializerContext.Default.UserSnapshotEnvelope)
            ?? throw new InvalidDataException("The retained snapshot envelope is invalid.");
        if (row.UserId != envelope.UserId || row.OriginDeviceId != envelope.OriginDeviceId ||
            row.OriginInstanceId != envelope.OriginInstanceId || row.OriginRevision != envelope.OriginRevision ||
            row.UserKeyEpoch != envelope.UserKeyEpoch || row.MembershipEpoch != envelope.MembershipEpoch ||
            row.CreatedAtUtc != envelope.CreatedAtUtc || !Hashing.Verify(row.SnapshotHash, envelope.SnapshotHash) ||
            !Hashing.Verify(row.OriginSignPublicKey, envelope.OriginSignPublicKey) ||
            !Hashing.Verify(row.OriginSignature, envelope.OriginSignature))
            throw new InvalidDataException("The retained snapshot row conflicts with its immutable envelope.");
        return envelope;
    }
}
