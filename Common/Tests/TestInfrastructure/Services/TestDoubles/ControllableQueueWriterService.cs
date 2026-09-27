using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using NSecKey = NSec.Cryptography.Key;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text.Json;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.Fixtures;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class ControllableQueueWriterService : ISyncQueueWriterService
{
    private readonly ISyncQueueWriterService _inner;

    public ControllableQueueWriterService(ISyncQueueWriterService inner) => _inner = inner;

    public bool FailNextEnqueue { get; set; }

    public Task EnqueueAsync(
        SyncItem item,
        long changedAtTs,
        IReadOnlyCollection<Guid> excludedDeviceIds,
        bool touchLocalSyncState,
        bool activateTargets,
        CancellationToken ct = default)
    {
        if (FailNextEnqueue)
        {
            FailNextEnqueue = false;
            throw new InvalidOperationException("Injected recovery queue failure.");
        }

        return _inner.EnqueueAsync(
            item,
            changedAtTs,
            excludedDeviceIds,
            touchLocalSyncState,
            activateTargets,
            ct);
    }

    public Task EnqueueForDeviceAsync(
        SyncItem item,
        Guid targetDeviceId,
        CancellationToken ct = default) =>
        _inner.EnqueueForDeviceAsync(item, targetDeviceId, ct);
}
