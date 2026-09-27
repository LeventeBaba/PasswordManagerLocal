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
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

namespace PasswordManagerLocal.Common.Tests.TestInfrastructure.Services.TestDoubles;

internal sealed class FixedDatabaseHealthService : IDatabaseHealthService
{
    private readonly DatabaseHealthCheckResult _result;

    public FixedDatabaseHealthService(bool healthy, string code) =>
        _result = new DatabaseHealthCheckResult(healthy, code);

    public Task<DatabaseHealthCheckResult> CheckAsync(CancellationToken ct = default) =>
        Task.FromResult(_result);
}
