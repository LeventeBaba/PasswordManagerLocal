using System.Security.Cryptography;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Internal.Recovery;

/// <summary>Commits approval to recovery evidence, excluding observation and retry bookkeeping.</summary>
internal static class SelfDiagnosticsRecoveryPlanFingerprint
{
    public static string Calculate(SelfDiagnosticsRecoveryPlan plan, Guid token, Guid deviceId, Guid originId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan.Archive, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot);
        using var stable = new MemoryStream();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            using (var writer = new Utf8JsonWriter(stable))
            {
                writer.WriteStartObject();
                foreach (var section in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(section.Name);
                    if (section.Value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WriteStartArray();
                        foreach (var row in section.Value.EnumerateArray()) WriteRow(writer, section.Name, row);
                        writer.WriteEndArray();
                    }
                    else WriteRow(writer, section.Name, section.Value);
                }
                writer.WriteEndObject();
            }
            return Convert.ToHexString(Hashing.SHA256Hash(hash =>
            {
                hash.WriteString("PasswordManagerLocal.ProfileRecovery.v2");
                hash.Write(token); hash.Write(deviceId); hash.Write(originId);
                hash.WriteBytes(stable.GetBuffer().AsSpan(0, checked((int)stable.Length)));
                hash.WriteString(string.Join("|", plan.Risks));
                hash.WriteString(string.Join("|", plan.Blockers));
            }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(stable.GetBuffer().AsSpan(0, checked((int)stable.Length)));
        }
    }

    private static void WriteRow(Utf8JsonWriter writer, string section, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) { row.WriteTo(writer); return; }
        writer.WriteStartObject();
        foreach (var field in row.EnumerateObject())
            if (!IsBookkeeping(section, field.Name)) field.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static bool IsBookkeeping(string section, string field) => section switch
    {
        nameof(UserRecoveryJournalSnapshot.Faults) => field is
            nameof(UserSyncFault.LastDetectedAtUtc) or nameof(UserSyncFault.LastRecoveryAttemptAtUtc) or
            nameof(UserSyncFault.NextRecoveryAttemptAtUtc) or nameof(UserSyncFault.RecoveryAttemptCount),
        nameof(UserRecoveryJournalSnapshot.Operations) => field is
            nameof(UserControlOperation.ReceivedAtUtc) or nameof(UserControlOperation.LastReceivedFromDeviceId),
        nameof(UserRecoveryJournalSnapshot.Snapshots) => field is
            nameof(UserSyncSnapshot.ReceivedAtUtc) or nameof(UserSyncSnapshot.LastReceivedFromDeviceId),
        nameof(UserRecoveryJournalSnapshot.Knowledge) => field == nameof(UserRevisionKnowledge.LastUpdatedAtUtc),
        nameof(UserRecoveryJournalSnapshot.ControlState) => field == nameof(UserControlState.LastUpdatedAtUtc),
        nameof(UserRecoveryJournalSnapshot.SyncState) => field == nameof(UserSyncState.LastUpdatedAtUtc),
        nameof(UserRecoveryJournalSnapshot.EnrollmentCommits) => field == nameof(DeviceEnrollmentCommit.LastAttemptAtUtc),
        _ => false
    };
}
