using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Security.Cryptography;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class ThrowingDeletionCleanupService : PasswordManagerLocal.Common.Backend.Abstractions.Services.IUserAccountDeletionCleanupService
{
    public Task DeleteCanonicalAndPendingStateAsync(Guid userId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Injected cleanup failure.");
}
