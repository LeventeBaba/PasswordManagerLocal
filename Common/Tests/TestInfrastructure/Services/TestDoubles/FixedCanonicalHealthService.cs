using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class FixedCanonicalHealthService : IUserCanonicalHealthService
{
    private readonly CanonicalHealthResult _result;

    public FixedCanonicalHealthService(CanonicalHealthResult result) => _result = result;

    public Task<CanonicalHealthResult> VerifyAsync(
        User user,
        EncryptionKey? key,
        UserSyncKeyConfidence keyConfidence,
        bool recordFault,
        CancellationToken ct = default) => Task.FromResult(_result);

    public Task UpdateCheckpointAsync(User user, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCheckpointAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
}
