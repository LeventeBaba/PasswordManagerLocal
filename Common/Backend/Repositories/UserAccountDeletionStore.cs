using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;

namespace PasswordManagerLocal.Common.Backend.Repositories;

internal sealed class UserAccountDeletionStore(
    SqliteSession session,
    IUserRepository users,
    IDeviceEnrollmentCommitRepository enrollments) : IUserAccountDeletionStore
{
    private const string ExclusiveLinkedDeviceIds = """
        SELECT d."Id" FROM "Devices" d
        JOIN "UserDevices" ud ON ud."DeviceId" = d."Id" AND ud."UserId" = $user
        WHERE NOT EXISTS (SELECT 1 FROM "UserDevices" other WHERE other."DeviceId" = d."Id"
                          AND other."UserId" <> $user AND other."IsDeleted" = 0)
        """;

    private const string ObsoleteModelPairs = $"""
        ("ModelType" = $userType AND "ModelId" = $user)
        OR ("ModelType" = $deviceType AND "ModelId" IN ({ExclusiveLinkedDeviceIds}))
        """;

    public async Task DeleteDependentStateAsync(Guid userId, CancellationToken ct)
    {
        if (!session.HasActiveTransaction)
            throw new InvalidOperationException("Account cleanup requires the authoritative deletion transaction.");

        // The caller's writer transaction fixes the ownership view until commit.
        // Clear model bookkeeping before removing the links that identify its scope.
        using (var command = await session.CreateCommandAsync($"""
            DELETE FROM "SyncItems" WHERE {ObsoleteModelPairs}
                OR ("ModelType" = $linkType AND "ModelId" IN
                    (SELECT "ModelId" FROM "UserDevices" WHERE "UserId" = $user));
            DELETE FROM "SyncTombstones" WHERE {ObsoleteModelPairs};
            """, ct))
        {
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$userType", (byte)SyncModelType.User);
            command.Parameters.AddWithValue("$deviceType", (byte)SyncModelType.Device);
            command.Parameters.AddWithValue("$linkType", (byte)SyncModelType.UserDevice);
            await command.ExecuteNonQueryAsync(ct);
        }

        // Explicit predicates also clean residual mutable state on idempotent re-entry.
        // Signed control operations, control state, membership history and cutoffs survive.
        using (var command = await session.CreateCommandAsync("""
            DELETE FROM "UserRecoveryJournals" WHERE "UserId" = $user;
            DELETE FROM "UserSyncSnapshots" WHERE "UserId" = $user;
            DELETE FROM "UserRevisionKnowledge" WHERE "UserId" = $user;
            DELETE FROM "UserSyncStates" WHERE "UserId" = $user;
            DELETE FROM "UserCanonicalCheckpoints" WHERE "UserId" = $user;
            DELETE FROM "UserSyncFaults" WHERE "UserId" = $user;
            """, ct))
        {
            command.Parameters.AddWithValue("$user", userId);
            await command.ExecuteNonQueryAsync(ct);
        }

        // Use ordinary row writes for enrollment commits so their
        // notifications remain staged until the deletion commits.
        enrollments.DeleteRange(await enrollments.ListForUserAsync(userId, ct));
    }

    public void StageUserDelete(User user) => users.Delete(user);
}
