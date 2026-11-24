using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// Stores exact received revocations before membership verification. The encrypted vault owns the
/// payload; a process-local cache only accelerates the durable profile fence. There is no success ACK here.
/// </summary>
internal sealed class CriticalControlIngressStore(SqliteConnectionFactory connections,
    IRevocationDeliveryActivity? deliveryActivity = null) : ICriticalControlIngressStore, IDisposable
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly object _initialization = new();
    private HashSet<Guid> _suspended = [];
    private bool _initialized;
    private bool _uncertain;

    public void Initialize()
    {
        lock (_initialization)
        {
            if (_initialized) return;
            using var connection = connections.CreateConnection();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EnvelopeHash, UserId, OperationId, OperationHash, TransportPeerDeviceId, EnvelopePayload FROM CriticalControlIngress;";
            using var reader = command.ExecuteReader();
            var users = new HashSet<Guid>();
            while (reader.Read())
            {
                var entry = ReadEntry(reader, 1);
                ValidateEntry(entry, reader.GetFieldValue<byte[]>(0));
                users.Add(entry.UserId);
            }
            Volatile.Write(ref _suspended, users);
            _initialized = true;
        }
    }

    public IReadOnlyList<Guid> SuspendedUserIds
    {
        get { Initialize(); return Volatile.Read(ref _suspended).Order().ToArray(); }
    }

    public bool IsSuspended(Guid userId)
    {
        Initialize();
        return Volatile.Read(ref _uncertain) || Volatile.Read(ref _suspended).Contains(userId);
    }

    public bool HasPendingOrUncertain
    {
        get { Initialize(); return Volatile.Read(ref _uncertain) || Volatile.Read(ref _suspended).Count != 0; }
    }

    public void ThrowIfSuspended(Guid userId)
    {
        if (IsSuspended(userId)) throw new PendingSecurityOperationException();
    }

    public async Task<byte[]> RetainAsync(UserControlOperationEnvelope envelope, Guid transportPeerDeviceId, CancellationToken ct = default)
    {
        UserControlOperationEnvelopeUtil.ValidateStructureAndHash(envelope);
        if (envelope.OperationType is not (UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion) ||
            transportPeerDeviceId == Guid.Empty)
            throw new InvalidDataException("Critical ingress requires a revocation and an authenticated transport identity.");
        // Different auxiliary proof variants have distinct custody identities. An invalid augmentation
        // can never overwrite the earlier received envelope or remove its fence.
        var payload = UserControlOperationEnvelopeUtil.Serialize(envelope);
        var hash = SHA256.HashData(payload);
        Initialize();
        await _writer.WaitAsync(ct);
        try
        {
            await using var connection = connections.CreateConnection();
            await connection.OpenAsync(ct);
            using (var known = connection.CreateCommand())
            {
                // An exact already-applied core cannot introduce unknown revocation authority.
                // Avoid a new ingress insert/delete pair on every unchanged anti-entropy replay.
                known.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM UserControlOperations
                    WHERE UserId = $user AND OperationId = $operation AND OperationHash = $hash AND Status = $applied);
                    """;
                known.Parameters.AddWithValue("$user", envelope.UserId);
                known.Parameters.AddWithValue("$operation", envelope.OperationId);
                known.Parameters.AddWithValue("$hash", envelope.OperationHash);
                known.Parameters.AddWithValue("$applied", (byte)UserControlOperationStatus.Applied);
                if ((long)(await known.ExecuteScalarAsync(ct))! != 0) return hash;
            }
            var pending = new HashSet<Guid>(Volatile.Read(ref _suspended)) { envelope.UserId };
            Volatile.Write(ref _suspended, pending);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO CriticalControlIngress (EnvelopeHash, UserId, OperationId, OperationHash, TransportPeerDeviceId, EnvelopePayload)
                VALUES ($hash, $user, $operation, $operationHash, $peer, $payload)
                ON CONFLICT (EnvelopeHash) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$user", envelope.UserId);
            command.Parameters.AddWithValue("$operation", envelope.OperationId);
            command.Parameters.AddWithValue("$operationHash", envelope.OperationHash);
            command.Parameters.AddWithValue("$peer", transportPeerDeviceId);
            command.Parameters.AddWithValue("$payload", payload);
            var changed = await command.ExecuteNonQueryAsync(ct);
            RefreshUsers(connection);
            if (changed != 0) deliveryActivity?.NotifyCommittedChange();
            return hash;
        }
        catch (Exception failure)
        {
            RefreshAfterFailure();
            if (failure is SqliteException sqlite && SqliteCapacityError.IsCapacityFailure(sqlite))
                throw SqliteCapacityError.Wrap(sqlite);
            throw;
        }
        finally { _writer.Release(); }
    }

    public Task DiscardAsync(byte[] envelopeHash, CancellationToken ct = default) =>
        DeleteAsync("EnvelopeHash = $hash", ct, ("$hash", envelopeHash));

    public Task CompleteAsync(Guid userId, Guid operationId, byte[] operationHash, CancellationToken ct = default) =>
        DeleteAsync("""
            UserId = $user AND OperationId = $operation AND OperationHash = $hash AND
            (EXISTS(SELECT 1 FROM UserControlOperations c WHERE c.UserId = $user AND c.OperationId = $operation
                AND ((c.Status = $applied AND c.OperationHash = $hash) OR
                     (c.Status = $quarantined AND (c.OperationHash = $hash OR c.ConflictingOperationHash = $hash))))
             OR EXISTS(SELECT 1 FROM DeletedUserBarriers b WHERE b.UserId = $user))
            """, ct, ("$user", userId), ("$operation", operationId), ("$hash", operationHash),
            ("$applied", (byte)UserControlOperationStatus.Applied), ("$quarantined", (byte)UserControlOperationStatus.Quarantined));

    private async Task DeleteAsync(string predicate, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        Initialize();
        await _writer.WaitAsync(ct);
        try
        {
            await using var connection = connections.CreateConnection();
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CriticalControlIngress WHERE " + predicate + ";";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            var changed = await command.ExecuteNonQueryAsync(ct);
            RefreshUsers(connection);
            if (changed != 0) deliveryActivity?.NotifyCommittedChange();
        }
        catch (Exception failure)
        {
            RefreshAfterFailure();
            if (failure is SqliteException sqlite && SqliteCapacityError.IsCapacityFailure(sqlite))
                throw SqliteCapacityError.Wrap(sqlite);
            throw;
        }
        finally { _writer.Release(); }
    }

    public async Task<IReadOnlyList<byte[]>> ListKeysAsync(CancellationToken ct = default)
    {
        Initialize();
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EnvelopeHash FROM CriticalControlIngress ORDER BY UserId, OperationId, EnvelopeHash;";
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<byte[]>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetFieldValue<byte[]>(0));
        return result;
    }

    public async Task<CriticalControlIngressEntry?> ReadAsync(byte[] envelopeHash, CancellationToken ct = default)
    {
        Initialize();
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UserId, OperationId, OperationHash, TransportPeerDeviceId, EnvelopePayload FROM CriticalControlIngress WHERE EnvelopeHash = $hash;";
        command.Parameters.AddWithValue("$hash", envelopeHash);
        using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var entry = ReadEntry(reader, 0);
        ValidateEntry(entry, envelopeHash);
        return entry;
    }

    public async Task<bool> CanApplyPredecessorAsync(UserControlOperationEnvelope candidate, CancellationToken ct = default)
    {
        if (!IsSuspended(candidate.UserId)) return true;
        if (Volatile.Read(ref _uncertain)) return false;
        await using var connection = connections.CreateConnection();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EnvelopeHash, UserId, OperationId, OperationHash, TransportPeerDeviceId, EnvelopePayload FROM CriticalControlIngress WHERE UserId = $user;";
        command.Parameters.AddWithValue("$user", candidate.UserId);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = ReadEntry(reader, 1);
            ValidateEntry(entry, reader.GetFieldValue<byte[]>(0));
            var pending = UserControlOperationEnvelopeUtil.Deserialize(entry.EnvelopePayload);
            // This only narrows admission. Ordinary author/signature/transition validation still
            // determines authority. Do not advance beyond an unresolved revocation's predecessor.
            if (candidate.ResultingKeyEpoch > pending.PreviousKeyEpoch ||
                candidate.ResultingMembershipEpoch > pending.PreviousMembershipEpoch) return false;
            if (pending.OperationType == UserControlOperationType.DeviceRemoval)
            {
                var removal = UserControlOperationEnvelopeUtil.DeserializeDeviceRemovalPayload(pending.OperationPayload);
                if (candidate.OriginDeviceId == removal.RemovedDeviceId)
                {
                    var maximum = removal.Origins.Where(origin => origin.OriginInstanceId == candidate.OriginInstanceId)
                        .Select(origin => origin.HighestAcceptedControlSequence).DefaultIfEmpty(-1).Max();
                    if (candidate.OriginSequence > maximum) return false;
                }
            }
        }
        return true;
    }

    private void RefreshAfterFailure()
    {
        try
        {
            using var connection = connections.CreateConnection();
            connection.Open();
            RefreshUsers(connection);
        }
        catch
        {
            // An unknown write outcome cannot reopen access. The global latch is finite and
            // a later successful ingress reconciliation can refresh it from committed state.
            Volatile.Write(ref _uncertain, true);
            Volatile.Write(ref _suspended, new HashSet<Guid>());
        }
    }

    private void RefreshUsers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT UserId FROM CriticalControlIngress;";
        using var reader = command.ExecuteReader();
        var users = new HashSet<Guid>();
        while (reader.Read()) users.Add(reader.GetGuid(0));
        Volatile.Write(ref _suspended, users);
        Volatile.Write(ref _uncertain, false);
    }

    private static CriticalControlIngressEntry ReadEntry(SqliteDataReader reader, int offset) => new(
        reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetFieldValue<byte[]>(offset + 2),
        reader.GetGuid(offset + 3), reader.GetFieldValue<byte[]>(offset + 4));

    private static void ValidateEntry(CriticalControlIngressEntry entry, byte[] hash)
    {
        if (entry.EnvelopePayload.Length == 0 || entry.EnvelopePayload.Length > SyncConstants.MaxUserControlOperationEnvelopeBytes ||
            hash.Length != 32 || !Hashing.Verify(hash, SHA256.HashData(entry.EnvelopePayload)))
            throw new InvalidDataException("Retained critical ingress is corrupt; ordinary access remains disabled.");
        var envelope = UserControlOperationEnvelopeUtil.Deserialize(entry.EnvelopePayload);
        if (entry.UserId != envelope.UserId || entry.OperationId != envelope.OperationId ||
            !Hashing.Verify(entry.OperationHash, envelope.OperationHash) || entry.TransportPeerDeviceId == Guid.Empty ||
            envelope.OperationType is not (UserControlOperationType.DeviceRemoval or UserControlOperationType.AccountDeletion))
            throw new InvalidDataException("Retained critical ingress identity is invalid; ordinary access remains disabled.");
    }

    public void Dispose() => _writer.Dispose();
}
