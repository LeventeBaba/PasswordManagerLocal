using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserSnapshotBatchVerificationServiceTests : IUserDataBundleVerificationService
{
    private readonly object _startedLock = new();
    private readonly List<long> _startedRevisions = [];
    private TaskCompletionSource<bool> _twoStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeVerifications;
    private int _maxActiveVerifications;

    [TestInitialize]
    public void Reset()
    {
        lock (_startedLock)
            _startedRevisions.Clear();

        _twoStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _activeVerifications = 0;
        _maxActiveVerifications = 0;
    }

    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Sync")]
    public async Task VerifyAsync_BoundsConcurrencyAndPreservesInputOrder()
    {
        using var key = EncryptionKey.Create();
        var snapshots = Enumerable.Range(1, 5)
            .Select(index => new UserSnapshotEnvelope
            {
                OriginRevision = index
            })
            .ToArray();
        var service = new UserSnapshotBatchVerificationService(this);

        var verificationTask = service.VerifyAsync(
            snapshots,
            key,
            UserSyncKeyConfidence.ExplicitlyTrusted);

        await _twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (_startedLock)
            MSTestAssert.AreEqual(
                SyncConstants.MaxConcurrentSnapshotVerifications,
                _startedRevisions.Count);
        MSTestAssert.AreEqual(
            SyncConstants.MaxConcurrentSnapshotVerifications,
            Volatile.Read(ref _maxActiveVerifications));

        _release.TrySetResult(true);
        var results = await verificationTask;

        try
        {
            MSTestAssert.AreEqual(snapshots.Length, results.Count);
            MSTestAssert.AreEqual(
                SyncConstants.MaxConcurrentSnapshotVerifications,
                Volatile.Read(ref _maxActiveVerifications));

            for (var index = 0; index < snapshots.Length; index++)
            {
                MSTestAssert.AreEqual(
                    $"snapshot-{snapshots[index].OriginRevision}",
                    results[index].DiagnosticCode);
            }
        }
        finally
        {
            foreach (var result in results)
                result.Dispose();
        }
    }

    public Task<UserDataBundleVerificationResult> VerifyCanonicalAsync(
        User user,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    public async Task<UserDataBundleVerificationResult> VerifySnapshotAsync(
        UserSnapshotEnvelope snapshot,
        EncryptionKey key,
        UserSyncKeyConfidence keyConfidence,
        CancellationToken ct = default)
    {
        var active = Interlocked.Increment(ref _activeVerifications);
        UpdateMaxActive(active);

        lock (_startedLock)
            _startedRevisions.Add(snapshot.OriginRevision);

        if (active == SyncConstants.MaxConcurrentSnapshotVerifications)
            _twoStarted.TrySetResult(true);

        try
        {
            await _release.Task.WaitAsync(ct);
            await Task.Delay(
                TimeSpan.FromMilliseconds((6 - snapshot.OriginRevision) * 10),
                ct);
            return new UserDataBundleVerificationResult
            {
                State = UserDataVerificationState.AuthorizationFailure,
                KeyConfidence = keyConfidence,
                DiagnosticCode = $"snapshot-{snapshot.OriginRevision}"
            };
        }
        finally
        {
            Interlocked.Decrement(ref _activeVerifications);
        }
    }

    private void UpdateMaxActive(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxActiveVerifications);
            if (candidate <= current)
                return;

            if (Interlocked.CompareExchange(
                    ref _maxActiveVerifications,
                    candidate,
                    current) == current)
                return;
        }
    }
}
