using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Utils;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class SyncVersionClockService : ISyncVersionClockService
{
    private const int MaxPersistenceAttempts = 8;
    private const int ObservationCommandTimeoutSeconds = 1;
    private static readonly object Gate = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _identity;
    private readonly TimeProvider _timeProvider;
    private SyncVersionStamp? _pendingObservedMaximum;

    public SyncVersionClockService(
        IServiceScopeFactory scopeFactory,
        IDeviceIdentityService identity,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
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
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var state = GetOrCreateState(db);
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
                try
                {
                    db.SaveChanges();
                    ClearPersistedPendingObservation(state);
                    return new SyncVersionStamp
                    {
                        PhysicalTimeUnixMilliseconds = state.LastPhysicalTimeUnixMilliseconds,
                        LogicalCounter = state.LastLogicalCounter,
                        OriginDeviceId = _identity.LocalDeviceId,
                        OriginInstanceId = _identity.OriginInstanceId
                    };
                }
                catch (DbUpdateConcurrencyException) when (attempt + 1 < MaxPersistenceAttempts)
                {
                }
                catch (DbUpdateException) when (attempt + 1 < MaxPersistenceAttempts)
                {
                    // A second process may have inserted the singleton row first.
                }
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

        lock (Gate)
        {
            RememberObservedMaximum(maximum);

            for (var attempt = 0; attempt < MaxPersistenceAttempts; attempt++)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var databaseKey = AppDatabaseTransactionRegistry.GetDatabaseKey(db);

                // Verification and merge services legitimately observe authenticated item versions
                // while their caller owns a transaction on the same SQLite database. Persisting the
                // clock through this service's separate DbContext at that point would self-deadlock:
                // the outer transaction waits for Observe, while Observe waits for that transaction.
                // Retain the maximum in memory and let the next post-transaction Observe/Next call
                // make it durable.
                if (AppDatabaseTransactionRegistry.HasActiveTransaction(databaseKey))
                    return;

                var originalCommandTimeout = db.Database.GetCommandTimeout();
                db.Database.SetCommandTimeout(ObservationCommandTimeoutSeconds);
                try
                {
                    var state = GetOrCreateState(db);
                    var pending = _pendingObservedMaximum;
                    if (pending is null || CompareClockPosition(pending, state) <= 0)
                    {
                        _pendingObservedMaximum = null;
                        return;
                    }

                    state.LastPhysicalTimeUnixMilliseconds = pending.PhysicalTimeUnixMilliseconds;
                    state.LastLogicalCounter = pending.LogicalCounter;
                    state.LastUpdatedAtUtc = _timeProvider.GetUtcNow();
                    checked { state.Version++; }

                    db.SaveChanges();
                    ClearPersistedPendingObservation(state);
                    return;
                }
                catch (Exception ex) when (IsSqliteBusy(ex))
                {
                    // Another process or a narrow transaction-start race may still own SQLite.
                    // Keep the authenticated floor in memory rather than blocking the caller.
                    return;
                }
                catch (DbUpdateConcurrencyException) when (attempt + 1 < MaxPersistenceAttempts)
                {
                }
                catch (DbUpdateException) when (attempt + 1 < MaxPersistenceAttempts)
                {
                    // A second process may have inserted the singleton row first.
                }
                finally
                {
                    db.Database.SetCommandTimeout(originalCommandTimeout);
                }
            }
        }

        throw new InvalidOperationException("The synchronization item clock could not observe a remote version atomically.");
    }

    private void RememberObservedMaximum(SyncVersionStamp maximum)
    {
        if (_pendingObservedMaximum is not null &&
            SyncVersionStampComparer.Instance.Compare(maximum, _pendingObservedMaximum) <= 0)
        {
            return;
        }

        _pendingObservedMaximum = new SyncVersionStamp
        {
            PhysicalTimeUnixMilliseconds = maximum.PhysicalTimeUnixMilliseconds,
            LogicalCounter = maximum.LogicalCounter,
            OriginDeviceId = maximum.OriginDeviceId,
            OriginInstanceId = maximum.OriginInstanceId
        };
    }

    private void ApplyPendingObservedFloor(SyncVersionClockState state)
    {
        var pending = _pendingObservedMaximum;
        if (pending is null || CompareClockPosition(pending, state) <= 0)
            return;

        state.LastPhysicalTimeUnixMilliseconds = pending.PhysicalTimeUnixMilliseconds;
        state.LastLogicalCounter = pending.LogicalCounter;
    }

    private void ClearPersistedPendingObservation(SyncVersionClockState state)
    {
        if (_pendingObservedMaximum is not null &&
            CompareClockPosition(_pendingObservedMaximum, state) <= 0)
        {
            _pendingObservedMaximum = null;
        }
    }

    private static int CompareClockPosition(SyncVersionStamp stamp, SyncVersionClockState state)
    {
        var physical = stamp.PhysicalTimeUnixMilliseconds.CompareTo(state.LastPhysicalTimeUnixMilliseconds);
        return physical != 0 ? physical : stamp.LogicalCounter.CompareTo(state.LastLogicalCounter);
    }

    private static bool IsSqliteBusy(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
                return true;
        }

        return false;
    }

    private void EnsureIdentityInitialized()
    {
        if (!_identity.IsInitialized || _identity.LocalDeviceId == Guid.Empty || _identity.OriginInstanceId == Guid.Empty)
            throw new InvalidOperationException("The local device identity must be initialized before issuing item versions.");
    }

    private SyncVersionClockState GetOrCreateState(AppDbContext db)
    {
        var state = db.SyncVersionClockStates.SingleOrDefault(row => row.Id == SyncVersionClockState.SingletonId);
        if (state is not null)
            return state;

        state = new SyncVersionClockState();
        db.SyncVersionClockStates.Add(state);
        return state;
    }
}
