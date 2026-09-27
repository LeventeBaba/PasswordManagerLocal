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

internal sealed class ControllablePendingActivationService : IPendingSyncActivationService
{
    private readonly IPendingSyncActivationService _inner;

    public ControllablePendingActivationService(IPendingSyncActivationService inner) => _inner = inner;

    public bool FailNextActivation { get; set; }

    public void ActivateDevices(IReadOnlyList<Device> devices) => _inner.ActivateDevices(devices);

    public Task ActivatePendingAsync(CancellationToken ct = default)
    {
        if (FailNextActivation)
        {
            FailNextActivation = false;
            throw new InvalidOperationException("Injected post-commit activation failure.");
        }

        return _inner.ActivatePendingAsync(ct);
    }
}
