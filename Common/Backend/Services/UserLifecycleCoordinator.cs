using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using System.Collections.Concurrent;

using PasswordManagerLocal.Common.Backend.Internal.Lifecycle;
namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Reference-counted keyed lifecycle execution. The callback form keeps the async-flow context
/// inside the coordinator, making nested lifecycle calls safely reentrant. Entries are retired
/// and removed when the last holder/waiter leaves, without allowing a removed entry to be
/// reacquired concurrently with a newly-created entry for the same user.
/// </summary>
public sealed class UserLifecycleCoordinator : IUserLifecycleCoordinator
{
    private readonly ConcurrentDictionary<Guid, UserLifecycleLockEntry> _entries = new();
    private readonly AsyncLocal<IReadOnlyDictionary<Guid, int>?> _heldDepths = new();

    public Task ExecuteAsync(
        Guid userId,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default) =>
        ExecuteAsync<object?>(
            userId,
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return null;
            },
            ct);

    public async Task<T> ExecuteAsync<T>(
        Guid userId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        ArgumentNullException.ThrowIfNull(action);

        var depths = _heldDepths.Value;
        if (depths is not null && depths.TryGetValue(userId, out var currentDepth))
        {
            SetDepth(userId, checked(currentDepth + 1));
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            finally
            {
                SetDepth(userId, currentDepth);
            }
        }

        var entry = AcquireEntry(userId);
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            ReleaseReference(userId, entry);
            throw;
        }

        SetDepth(userId, 1);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            SetDepth(userId, 0);
            entry.Semaphore.Release();
            ReleaseReference(userId, entry);
        }
    }

    private UserLifecycleLockEntry AcquireEntry(Guid userId)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(userId, static _ => new UserLifecycleLockEntry());
            lock (entry.ReferenceGate)
            {
                // A caller can obtain a dictionary value immediately before the final releaser
                // retires and removes it. Never acquire such an orphaned entry; retry against
                // the current dictionary value instead.
                if (entry.IsRetired)
                    continue;

                entry.ReferenceCount = checked(entry.ReferenceCount + 1);
                return entry;
            }
        }
    }

    private void SetDepth(Guid userId, int depth)
    {
        var replacement = _heldDepths.Value is null
            ? new Dictionary<Guid, int>()
            : _heldDepths.Value.ToDictionary(item => item.Key, item => item.Value);

        if (depth <= 0)
            replacement.Remove(userId);
        else
            replacement[userId] = depth;

        _heldDepths.Value = replacement.Count == 0 ? null : replacement;
    }

    private void ReleaseReference(Guid userId, UserLifecycleLockEntry entry)
    {
        var remove = false;
        lock (entry.ReferenceGate)
        {
            if (entry.ReferenceCount <= 0)
                throw new InvalidOperationException("The lifecycle lock reference count is corrupt.");

            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.IsRetired = true;
                remove = true;
            }
        }

        if (!remove)
            return;

        ((ICollection<KeyValuePair<Guid, UserLifecycleLockEntry>>)_entries)
            .Remove(new KeyValuePair<Guid, UserLifecycleLockEntry>(userId, entry));
        entry.Semaphore.Dispose();
    }

}
