using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Repositories.TestDoubles;

internal sealed class MemoryCheckpointRepository : IUserCanonicalCheckpointRepository
{
    private UserCanonicalCheckpoint? _checkpoint;

    public MemoryCheckpointRepository(UserCanonicalCheckpoint checkpoint) => _checkpoint = checkpoint;

    public Task<UserCanonicalCheckpoint?> GetAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_checkpoint?.UserId == userId ? _checkpoint : null);

    public Task AddAsync(UserCanonicalCheckpoint checkpoint, CancellationToken ct = default)
    {
        _checkpoint = checkpoint;
        return Task.CompletedTask;
    }

    public void Update(UserCanonicalCheckpoint checkpoint) => _checkpoint = checkpoint;

    public void Delete(UserCanonicalCheckpoint checkpoint) => _checkpoint = null;
}
