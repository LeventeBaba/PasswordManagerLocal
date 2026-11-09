using Microsoft.Data.Sqlite;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

internal sealed class SyncVersionClockService : ISyncVersionClockService
{
    private const int MaxPersistenceAttempts = 8;
    private const int ObservationCommandTimeoutSeconds = 1;
    private static readonly object Gate = new();

    private readonly ISyncVersionClockStore _store;
    private readonly IDeviceIdentityService _identity;
    private readonly TimeProvider _timeProvider;
    private SyncVersionStamp? _pendingObservedMaximum;

    public SyncVersionClockService(
        ISyncVersionClockStore store,
        IDeviceIdentityService identity,
        TimeProvider timeProvider)
    {
        _store = store;
        _identity = identity;
        _timeProvider = timeProvider;
    }

    public SyncVersionStamp Next()
    {
        lock (Gate)
        {
            EnsureIdentityInitialized();
            for (var attempt = 0; attempt < MaxPersistenceAttempts; attempt++)
            {
                var persisted = _store.Read();
                var expectedVersion = persisted?.Version;
                var state = persisted ?? new SyncVersionClockState();
                ApplyPendingObservedFloor(state);

                var now = _timeProvider.GetUtcNow();
                var nowMilliseconds = now.ToUnixTimeMilliseconds();

                if (nowMilliseconds > state.LastPhysicalTimeUnixMilliseconds)
                {
                    state.LastPhysicalTimeUnixMilliseconds = nowMilliseconds;
                    state.LastLogicalCounter = 0;
                }
                else
                {
                    checked { state.LastLogicalCounter++; }
                }

                state.LastUpdatedAtUtc = now;
                checked { state.Version++; }
                if (!_store.TryWrite(state, expectedVersion))
                    continue;

                ClearPersistedPendingObservation(state);
                return new SyncVersionStamp
                {
                    PhysicalTimeUnixMilliseconds = state.LastPhysicalTimeUnixMilliseconds,
                    LogicalCounter = state.LastLogicalCounter,
                    OriginDeviceId = _identity.LocalDeviceId,
                    OriginInstanceId = _identity.OriginInstanceId
                };
            }
        }

        throw new InvalidOperationException("The synchronization item clock could not be advanced atomically.");
    }

    public void Observe(IEnumerable<SyncVersionStamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        var maximum = stamps.Aggregate<SyncVersionStamp, SyncVersionStamp?>(
            null,
            (current, stamp) =>
            {
                SyncVersionStampComparer.Validate(stamp);
                return current is null || SyncVersionStampComparer.Instance.Compare(stamp, current) > 0
                    ? stamp
                    : current;
            });
        if (maximum is null)
            return;

        RememberObservedMaximum(maximum);
        var databaseKey = _store.DatabaseKey;

        // Defer before taking Gate. A concurrent Next can own Gate while waiting
        // for this very SQLite transaction to release its writer lock.
        if (AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey))
            return;

        lock (Gate)
        {
            for (var attempt = 0; attempt < MaxPersistenceAttempts; attempt++)
            {
                if (AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey))
                    return;

                try
                {
                    var persisted = _store.Read(ObservationCommandTimeoutSeconds);
                    var expectedVersion = persisted?.Version;
                    var state = persisted ?? new SyncVersionClockState();
                    var pending = Volatile.Read(ref _pendingObservedMaximum);
                    if (pending is null)
                        return;
                    if (CompareClockPosition(pending, state) <= 0)
                    {
                        ClearPersistedPendingObservation(state);
                        return;
                    }

                    state.LastPhysicalTimeUnixMilliseconds = pending.PhysicalTimeUnixMilliseconds;
                    state.LastLogicalCounter = pending.LogicalCounter;
                    state.LastUpdatedAtUtc = _timeProvider.GetUtcNow();
                    checked { state.Version++; }

                    if (!_store.TryWrite(state, expectedVersion, ObservationCommandTimeoutSeconds))
                        continue;

                    ClearPersistedPendingObservation(state);
                    return;
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                {
                    // A separate writer can still race the registry. Leave the floor pending.
                    return;
                }
            }
        }

        throw new InvalidOperationException("The synchronization item clock could not observe a remote version atomically.");
    }

    private void RememberObservedMaximum(SyncVersionStamp maximum)
    {
        var observed = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = maximum.PhysicalTimeUnixMilliseconds,
            LogicalCounter = maximum.LogicalCounter,
            OriginDeviceId = maximum.OriginDeviceId,
            OriginInstanceId = maximum.OriginInstanceId
        };

        // Publish an immutable copy without taking the database gate. A newer observation
        // can arrive while another thread is persisting an earlier floor.
        while (true)
        {
            var pending = Volatile.Read(ref _pendingObservedMaximum);
            if (pending is not null && SyncVersionStampComparer.Instance.Compare(observed, pending) <= 0)
                return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pendingObservedMaximum, observed, pending), pending))
                return;
        }
    }

    private void ApplyPendingObservedFloor(SyncVersionClockState state)
    {
        var pending = Volatile.Read(ref _pendingObservedMaximum);
        if (pending is null || CompareClockPosition(pending, state) <= 0)
            return;

        state.LastPhysicalTimeUnixMilliseconds = pending.PhysicalTimeUnixMilliseconds;
        state.LastLogicalCounter = pending.LogicalCounter;
    }

    private void ClearPersistedPendingObservation(SyncVersionClockState state)
    {
        var pending = Volatile.Read(ref _pendingObservedMaximum);
        if (pending is not null && CompareClockPosition(pending, state) <= 0)
            Interlocked.CompareExchange(ref _pendingObservedMaximum, null, pending);
    }

    private static int CompareClockPosition(SyncVersionStamp stamp, SyncVersionClockState state)
    {
        var physical = stamp.PhysicalTimeUnixMilliseconds.CompareTo(state.LastPhysicalTimeUnixMilliseconds);
        return physical != 0 ? physical : stamp.LogicalCounter.CompareTo(state.LastLogicalCounter);
    }

    private void EnsureIdentityInitialized()
    {
        if (!_identity.IsInitialized || _identity.LocalDeviceId == Guid.Empty || _identity.OriginInstanceId == Guid.Empty)
            throw new InvalidOperationException("The local device identity must be initialized before issuing item versions.");
    }
}
