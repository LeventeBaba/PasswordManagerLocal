using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;
using System.Text;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class ThrowingProjectionService : IUserLoginIdentityProjectionService
{
    private readonly IUserLoginIdentityProjectionService _inner;

    public ThrowingProjectionService(IUserLoginIdentityProjectionService inner) => _inner = inner;

    public Task<UserLoginIdentityState> SetCanonicalAsync(User user, SyncVersionStamp generalUserDataVersion, CancellationToken ct = default) =>
        _inner.SetCanonicalAsync(user, generalUserDataVersion, ct);

    public Task<UserLoginIdentityState?> RecalculateAsync(Guid userId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Injected projection persistence failure.");

    public Task<UserLoginIdentityState?> RecalculateUnderLifecycleAsync(Guid userId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Injected projection persistence failure.");

    public Task<UserLoginIdentityMatchResult> FindByUsernameAsync(byte[] normalizedUsername, CancellationToken ct = default) =>
        _inner.FindByUsernameAsync(normalizedUsername, ct);
}
