using System.Security.Cryptography;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.Internal.Recovery;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed partial class SelfDiagnosticsService
{
    private async Task<Guid> StageRecoveryJournalAsync(UserRecoveryJournalSnapshot snapshot, long targetKeyEpoch, bool confirmed, CancellationToken ct)
    {
        snapshot.JournalId = Guid.NewGuid();
        snapshot.CreatedAtUtc = DateTimeOffset.UtcNow;
        snapshot.TargetKeyEpoch = targetKeyEpoch;
        snapshot.Confirmed = confirmed;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, BackendJsonSerializerContext.Default.UserRecoveryJournalSnapshot);
        try { return await _recoveryJournal.StageAsync(snapshot.User.UId, bytes, ct); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
