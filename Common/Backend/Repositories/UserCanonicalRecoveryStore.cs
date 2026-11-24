using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Security;
using System.Security.Cryptography;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserCanonicalRecoveryStore(SqliteSession session) : IUserCanonicalRecoveryStore
{
    // Account-scoped dependencies, including absence, terminal evidence and newly arriving rows.
    // These are content comparisons, not timestamps, row versions or database-wide data_version.
    private static readonly (string Table, string Account, string Order)[] Dependencies =
    [
        ("Users", "UId", "UId"),
        ("UserDevices", "UserId", "DeviceId"),
        ("LocalUserDevices", "UserId", "LocalDeviceIdentityId"),
        ("UserCanonicalCheckpoints", "UserId", "UserId"),
        ("UserControlStates", "UserId", "UserId"),
        ("CriticalControlIngress", "UserId", "EnvelopeHash"),
        ("DeletedUserBarriers", "UserId", "UserId"),
        ("UserMembershipAuthorizations", "UserId", "AuthorizationId"),
        ("UserOriginRemovalCutoffs", "UserId", "CutoffId"),
        ("UserControlOperations", "UserId", "OperationId"),
        ("UserSyncFaults", "UserId", "Id"),
        ("UserSyncSnapshots", "UserId", "Id"),
        ("UserRecoveryJournals", "UserId", "Id"),
        ("UserSyncStates", "UserId", "UserId"),
        ("UserRevisionKnowledge", "UserId", "OriginDeviceId, OriginInstanceId, UserKeyEpoch"),
        ("UserLoginIdentityStates", "UserId", "UserId")
    ];

    public Task<UserRecoveryInput> CaptureAsync(Guid userId, CancellationToken ct = default)
    {
        if (session.HasActiveTransaction || session.HasActiveReadSnapshot || session.HasPendingChanges)
            throw new InvalidOperationException("Recovery capture requires a clean session without a read or write transaction.");
        return session.ReadSnapshotAsync(async () =>
        {
            User? user = null;
            IReadOnlyList<UserSyncSnapshot> candidates = [];
            IReadOnlyList<UserControlOperation> operations = [];
            try
            {
                var commitment = await ReadCommitmentAsync(userId, ct);
                user = await new UserRepository(session).GetByIdAsNoTrackingWithRelationsAsync(userId, ct);
                var checkpoint = await new UserCanonicalCheckpointRepository(session).GetAsync(userId, ct);
                var control = await new UserControlStateRepository(session).GetAsync(userId, ct);
                var snapshots = new UserSyncSnapshotRepository(session);
                // Epoch selection is authenticated after capture. Retain only the possible epochs
                // named by canonical/control/checkpoint evidence, then select the proven one.
                var epochs = new[] { user?.KeyEpoch ?? 0, checkpoint?.KeyEpoch ?? 0, control?.AppliedKeyEpoch ?? 0 };
                var rows = new List<UserSyncSnapshot>();
                candidates = rows;
                foreach (var epoch in epochs.Where(epoch => epoch > 0).Distinct())
                    rows.AddRange(await snapshots.ListRecoveryEvidenceAsync(userId, epoch, ct));
                operations = await new UserControlOperationRepository(session).ListForUserAsync(userId, ct);
                return new UserRecoveryInput(userId, user, checkpoint, control,
                    await new DeletedUserBarrierRepository(session).ExistsAsync(userId, ct),
                    await new UserSyncFaultRepository(session).ListActiveForUserAsync(userId, ct),
                    candidates, await snapshots.HasIsolatedForkAsync(userId, ct),
                    await session.ExistsAsync("SELECT 1 FROM CriticalControlIngress WHERE UserId = $user", ct, ("$user", userId)),
                    new UserRecoveryAuthority(
                        await new UserMembershipAuthorizationRepository(session).ListForUserAsync(userId, ct),
                        await new UserOriginRemovalCutoffRepository(session).ListForUserAsync(userId, ct), operations),
                    commitment);
            }
            catch
            {
                user?.ClearEncryptedPayloads();
                if (user?.SavedKey is { } saved) CryptographicOperations.ZeroMemory(saved);
                foreach (var row in candidates) CryptographicOperations.ZeroMemory(row.EnvelopePayload);
                foreach (var row in operations) CryptographicOperations.ZeroMemory(row.EnvelopePayload);
                throw;
            }
        }, ct);
    }

    public async Task<bool> IsCurrentAsync(UserRecoveryInput input, CancellationToken ct = default)
    {
        if (!session.HasActiveTransaction || session.HasPendingChanges)
            throw new InvalidOperationException("Recovery revalidation requires a clean immediate write transaction.");
        var currentCommitment = await ReadCommitmentAsync(input.UserId, ct);
        return Hashing.Verify(input.Commitment, currentCommitment);
    }

    private async Task<byte[]> ReadCommitmentAsync(Guid userId, CancellationToken ct)
    {
        using var hash = new Sha256HashBuilder();
        hash.WriteString("canonical-recovery-input-v1");
        hash.Write(userId);
        foreach (var (table, account, order) in Dependencies)
        {
            hash.WriteString(table);
            // All SQL identifiers come from the fixed dependency list above.
            using var command = await session.CreateCommandAsync(
                $"SELECT * FROM \"{table}\" WHERE \"{account}\" = $user ORDER BY {order};", ct);
            command.Parameters.AddWithValue("$user", userId);
            using var reader = await command.ExecuteReaderAsync(ct);
            hash.Write(reader.FieldCount);
            while (await reader.ReadAsync(ct))
            {
                hash.Write((byte)1);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.IsDBNull(column)) { hash.Write((byte)0); continue; }
                    var type = reader.GetFieldType(column);
                    if (type == typeof(byte[]))
                    {
                        hash.Write((byte)1);
                        // Match the repository BLOB read API without opening metadata-driven
                        // streaming handles. Retain only one temporary bounded value at a time.
                        var bytes = reader.GetFieldValue<byte[]>(column);
                        try
                        {
                            hash.Write((long)bytes.Length);
                            const int chunkBytes = 64 * 1024;
                            for (var offset = 0; offset < bytes.Length;)
                            {
                                ct.ThrowIfCancellationRequested();
                                var count = Math.Min(chunkBytes, bytes.Length - offset);
                                hash.WriteBytes(bytes.AsSpan(offset, count));
                                offset += count;
                            }
                        }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    else if (type == typeof(long)) { hash.Write((byte)2); hash.Write(reader.GetInt64(column)); }
                    else if (type == typeof(string)) { hash.Write((byte)3); hash.WriteString(reader.GetString(column)); }
                    else if (type == typeof(double)) { hash.Write((byte)4); hash.Write(BitConverter.DoubleToInt64Bits(reader.GetDouble(column))); }
                    else throw new InvalidDataException("Unsupported recovery dependency storage type.");
                }
            }
            hash.Write((byte)0);
        }
        return hash.GetHashAndReset();
    }
}
