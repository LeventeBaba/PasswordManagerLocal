using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Security;

namespace PasswordManagerLocal.Common.Backend.Repositories;

/// <summary>Reads only committed relationship/authority metadata, in one consistent SQLite snapshot.</summary>
internal sealed class SyncRuntimeEligibilityRepository(SqliteSession session, IDeviceIdentityService identity)
    : ISyncRuntimeEligibilityService
{
    // EXISTS prunes the usual idle path and excludes stale, deleted, conflicting and ended authority.
    // Neither LocalDeviceIdentity.IsSyncOn nor presence/endpoints are inputs to this decision.
    internal const string EligiblePairs = """
        FROM "LocalUserDevices" lu
        JOIN "Users" u ON u."UId" = lu."UserId"
        JOIN "UserDevices" ud ON ud."UserId" = lu."UserId"
        JOIN "Devices" d ON d."Id" = ud."DeviceId"
        WHERE lu."LocalDeviceIdentityId" = $local AND lu."IsSyncOn" = 1
          AND ud."DeviceId" <> $local AND ud."IsDeleted" = 0 AND ud."IsSyncOn" = 1
          AND d."IsTrusted" = 1 AND d."IsBlocked" = 0
          AND length(d."PublicKey") > 0 AND length(d."SignPublicKey") > 0
          AND length(d."TlsCertFingerprint") = 64
          AND NOT EXISTS (SELECT 1 FROM "DeletedUserBarriers" b WHERE b."UserId" = lu."UserId")
          AND NOT EXISTS (SELECT 1 FROM "CriticalControlIngress" ci WHERE ci."UserId" = lu."UserId")
          AND NOT EXISTS (SELECT 1 FROM "UserControlStates" s WHERE s."UserId" = lu."UserId" AND s."HasConflict" = 1)
          AND EXISTS (SELECT 1 FROM "UserMembershipAuthorizations" a
              WHERE a."UserId" = lu."UserId" AND a."DeviceId" = $local AND a."OriginInstanceId" = $origin
                AND a."IsActive" = 1 AND a."EndedMembershipEpoch" IS NULL
                AND a."AuthorizationId" <> $empty AND a."OriginInstanceId" <> $empty
                AND a."StartedMembershipEpoch" > 0 AND a."MinimumKeyEpoch" > 0
                AND a."StartedMembershipEpoch" <= u."MembershipEpoch" AND a."MinimumKeyEpoch" <= u."KeyEpoch"
                AND (a."MaximumKeyEpoch" IS NULL OR a."MaximumKeyEpoch" >= u."KeyEpoch")
                AND a."SignPublicKeyHash" = $localSignHash AND a."SignPublicKey" = $localSignKey
                AND a."AgreementPublicKeyHash" = $localAgreementHash
                AND a."TlsCertFingerprint" = $localFingerprint COLLATE NOCASE
                AND NOT EXISTS (SELECT 1 FROM "UserOriginRemovalCutoffs" c
                    WHERE c."UserId" = a."UserId" AND c."DeviceId" = a."DeviceId" AND c."OriginInstanceId" = a."OriginInstanceId"))
          AND EXISTS (SELECT 1 FROM "UserMembershipAuthorizations" a
              WHERE a."UserId" = lu."UserId" AND a."DeviceId" = d."Id"
                AND a."IsActive" = 1 AND a."EndedMembershipEpoch" IS NULL
                AND a."AuthorizationId" <> $empty AND a."OriginInstanceId" <> $empty
                AND a."StartedMembershipEpoch" > 0 AND a."MinimumKeyEpoch" > 0
                AND a."StartedMembershipEpoch" <= u."MembershipEpoch" AND a."MinimumKeyEpoch" <= u."KeyEpoch"
                AND (a."MaximumKeyEpoch" IS NULL OR a."MaximumKeyEpoch" >= u."KeyEpoch")
                AND a."SignPublicKeyHash" = d."SignPublicKeyHash" AND a."SignPublicKey" = d."SignPublicKey"
                AND a."TlsCertFingerprint" = d."TlsCertFingerprint" COLLATE NOCASE
                AND NOT EXISTS (SELECT 1 FROM "UserOriginRemovalCutoffs" c
                    WHERE c."UserId" = a."UserId" AND c."DeviceId" = a."DeviceId" AND c."OriginInstanceId" = a."OriginInstanceId"))
        """;

    public Task<SyncRuntimeEligibilityResult> EvaluateAsync(CancellationToken ct = default) =>
        session.ReadSnapshotAsync(async () =>
        {
            if (!identity.IsInitialized || identity.LocalDeviceId == Guid.Empty || identity.OriginInstanceId == Guid.Empty)
                throw new InvalidOperationException("The local synchronization identity is not initialized.");

            // A local-only genesis has nobody to answer. Passive ingress is justified
            // only by a remote historical identity the existing protocol can authenticate.
            var historical = await session.ExistsAsync("""
                SELECT 1 FROM "UserMembershipAuthorizations" a JOIN "Devices" d ON d."Id" = a."DeviceId"
                WHERE a."DeviceId" <> $local AND d."IsTrusted" = 1 AND d."IsBlocked" = 0
                  AND length(d."PublicKey") > 0 AND length(d."SignPublicKey") > 0
                  AND length(d."TlsCertFingerprint") = 64
                  AND a."SignPublicKeyHash" = d."SignPublicKeyHash"
                  AND a."TlsCertFingerprint" = d."TlsCertFingerprint" COLLATE NOCASE
                LIMIT 1
                """, ct, ("$local", identity.LocalDeviceId));
            if (!await session.ExistsAsync("""SELECT 1 FROM "LocalUserDevices" LIMIT 1""", ct))
                return new SyncRuntimeEligibilityResult(SyncRuntimeEligibilityReason.NoLocalProfiles, historical);
            if (!await session.ExistsAsync("""SELECT 1 FROM "LocalUserDevices" WHERE "IsSyncOn" = 1 LIMIT 1""", ct))
                return new SyncRuntimeEligibilityResult(SyncRuntimeEligibilityReason.LocalSynchronizationDisabled, historical);

            (string Name, object? Value)[] parameters =
            [
                ("$local", identity.LocalDeviceId), ("$origin", identity.OriginInstanceId), ("$empty", Guid.Empty),
                ("$localSignHash", Hashing.SHA256Hash(identity.SignPublicKey)), ("$localSignKey", identity.SignPublicKey),
                ("$localAgreementHash", Hashing.SHA256Hash(identity.AgreementPublicKey)),
                ("$localFingerprint", identity.FingerprintHex)
            ];
            if (await session.ExistsAsync("SELECT 1 " + EligiblePairs + " LIMIT 1", ct, parameters))
            {
                // Validate the qualifying rows before enabling networking. Hash checks cover flags,
                // keys and relationship identity without reading or decrypting any vault payload.
                var valid = await session.ReadAsync($"""
                    SELECT {LocalUserDeviceRepository.Projection}, {UserDeviceRepository.Projection}, {DeviceRepository.Projection}
                    {EligiblePairs} LIMIT 1;
                    """, row =>
                    {
                        _ = LocalUserDeviceRepository.ReadRow(row);
                        _ = UserDeviceRepository.ReadRow(row, 4);
                        var device = DeviceRepository.ReadRow(row, 12);
                        device.VerifyIntegrity();
                        return true;
                    }, ct, parameters);
                if (valid.Count != 0)
                    return new SyncRuntimeEligibilityResult(SyncRuntimeEligibilityReason.Eligible, historical);
            }

            var linked = await session.ExistsAsync("""
                SELECT 1 FROM "UserDevices" ud JOIN "LocalUserDevices" lu ON lu."UserId" = ud."UserId"
                WHERE ud."DeviceId" <> $local LIMIT 1
                """, ct, ("$local", identity.LocalDeviceId));
            return new SyncRuntimeEligibilityResult(linked ? SyncRuntimeEligibilityReason.NoEligibleRemoteDevices
                : SyncRuntimeEligibilityReason.NoRemoteDevices, historical);
        }, ct);
}
