using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class SelfDiagnosticsServiceTests
{
    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task CurrentDevice_NormalizesLegacyGenesisHash_WithoutChangingPasswords()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var username = "selfdiagnostics" + Guid.NewGuid().ToString("N")[..12];
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Retained credential", Description = "Preserved after diagnostics",
            Color = "#FF123456", Password = Encoding.UTF8.GetBytes("RetainedPassword123!"), TagIds = []
        });
        var userId = (await host.GetOnlyCanonicalCheckpointAsync())!.UserId;

        using (var scope = host.Services.CreateScope())
        {
            var authorizations = scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>();
            var genesis = (await authorizations.ListForUserAsync(userId)).Single();
            Assert.IsTrue(genesis.IsGenesis);
            genesis.AdditionOperationHash = [];
            genesis.Version = checked(genesis.Version + 1);
            authorizations.Update(genesis);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var first = await host.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(first.Healthy);
        Assert.AreEqual(1, first.RepairedCount);
        CollectionAssert.Contains(first.Findings.ToArray(), "GenesisHashNormalized");

        var second = await host.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(second.Healthy);
        Assert.AreEqual(0, second.RepairedCount);

        using (var scope = host.Services.CreateScope())
        {
            var genesis = (await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .ListForUserAsync(userId)).Single();
            Assert.IsNull(genesis.AdditionOperationHash);
        }
        Assert.IsTrue((await host.Endpoints.GetSavedPasswordsAsync(token)).Passwords
            .Any(item => item.Name == "Retained credential"));
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task TwoActiveDevices_RecoverSignedAdditionAndGenesisEvidence_WithoutChangingPasswords()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var target = await ProductionSyncTestHost.CreateAsync();
        source.ConnectTo(target);
        target.ConnectTo(source);
        var username = "diagnosticpair" + Guid.NewGuid().ToString("N")[..12];
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.EnableSyncAsync(token);
        await source.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Still present", Description = "Two-device repair", Color = "#FF123456",
            Password = Encoding.UTF8.GetBytes("PreservedPassword123!"), TagIds = []
        });
        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        var userId = (await source.GetOnlyCanonicalCheckpointAsync())!.UserId;
        using (var scope = source.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>();
            var rows = await repository.ListForUserAsync(userId);
            Assert.AreEqual(2, rows.Count(row => row.IsActive));
            var genesis = rows.Single(row => row.IsGenesis);
            var added = rows.Single(row => row.DeviceId == target.Identity.LocalDeviceId);
            genesis.AdditionOperationHash = [];
            added.AdditionOperationId = null;
            added.AdditionOperationHash = [];
            added.SignPublicKeyHash = [];
            genesis.Version++;
            added.Version++;
            repository.Update(genesis);
            repository.Update(added);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(3, result.RepairedCount);
        CollectionAssert.Contains(result.Findings.ToArray(), "GenesisHashNormalized");
        CollectionAssert.Contains(result.Findings.ToArray(), "AdditionEvidenceRecovered");
        CollectionAssert.Contains(result.Findings.ToArray(), "MembershipSignHashRecovered");
        using (var scope = source.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .ListForUserAsync(userId);
            Assert.AreEqual(2, rows.Count(row => row.IsActive));
            Assert.IsNull(rows.Single(row => row.IsGenesis).AdditionOperationHash);
            Assert.IsNotNull(rows.Single(row => row.DeviceId == target.Identity.LocalDeviceId).AdditionOperationId);
            CollectionAssert.AreEqual(
                PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(target.Identity.SignPublicKey),
                rows.Single(row => row.DeviceId == target.Identity.LocalDeviceId).SignPublicKeyHash);
        }
        Assert.IsTrue((await source.Endpoints.GetSavedPasswordsAsync(token)).Passwords
            .Any(password => password.Name == "Still present"));
        Assert.AreEqual(0, (await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token)).RepairedCount);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SignedRemoval_RestoresMissingCutoffButNeverReactivatesDevice()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var target = await ProductionSyncTestHost.CreateAsync();
        source.ConnectTo(target);
        target.ConnectTo(source);
        var username = "diagnosticcutoff" + Guid.NewGuid().ToString("N")[..12];
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.EnableSyncAsync(token);
        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        var userId = (await source.GetOnlyCanonicalCheckpointAsync())!.UserId;
        var removal = await source.Endpoints.DisconnectUserDeviceAsync(
            token, target.Identity.LocalDeviceId, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        Assert.IsTrue(removal.Removed);
        int removedCount;
        using (var scope = source.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = db.UserOriginRemovalCutoffs.Where(row => row.UserId == userId &&
                row.DeviceId == target.Identity.LocalDeviceId).ToArray();
            removedCount = rows.Length;
            Assert.IsTrue(removedCount > 0);
            db.UserOriginRemovalCutoffs.RemoveRange(rows);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(removedCount, result.RepairedCount);
        CollectionAssert.Contains(result.Findings.ToArray(), "RemovalCutoffRecovered");
        using (var scope = source.Services.CreateScope())
        {
            Assert.AreEqual(removedCount, (await scope.ServiceProvider
                .GetRequiredService<IUserOriginRemovalCutoffRepository>().ListForUserAsync(userId))
                .Count(row => row.DeviceId == target.Identity.LocalDeviceId));
            Assert.IsFalse((await scope.ServiceProvider
                .GetRequiredService<IUserMembershipAuthorizationRepository>().ListForUserAsync(userId))
                .Single(row => row.DeviceId == target.Identity.LocalDeviceId).IsActive);
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task CurrentGenesis_RebuildsOnlyDerivedSigningHashFromLocalKey()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var username = "diagnostichash" + Guid.NewGuid().ToString("N")[..12];
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        var userId = (await host.GetOnlyCanonicalCheckpointAsync())!.UserId;
        using (var scope = host.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>();
            var genesis = (await repository.ListForUserAsync(userId)).Single();
            genesis.SignPublicKeyHash = [];
            genesis.Version++;
            repository.Update(genesis);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var result = await host.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(1, result.RepairedCount);
        CollectionAssert.Contains(result.Findings.ToArray(), "LocalSignHashRecovered");
        using var verifyScope = host.Services.CreateScope();
        var restored = (await verifyScope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
            .ListForUserAsync(userId)).Single();
        CollectionAssert.AreEqual(
            PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(host.Identity.SignPublicKey),
            restored.SignPublicKeyHash);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task AmbiguousLocalIdentity_DoesNotApplyEvenTheGenesisNormalization()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var username = "diagnosticunsafe" + Guid.NewGuid().ToString("N")[..12];
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        var userId = (await host.GetOnlyCanonicalCheckpointAsync())!.UserId;
        using (var scope = host.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>();
            var genesis = (await repository.ListForUserAsync(userId)).Single();
            genesis.AdditionOperationHash = [];
            genesis.AgreementPublicKeyHash = [];
            genesis.Version++;
            repository.Update(genesis);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var result = await host.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsFalse(result.Healthy);
        Assert.AreEqual(0, result.RepairedCount);
        CollectionAssert.Contains(result.Findings.ToArray(), "LocalMembershipInvalid");
        using var verifyScope = host.Services.CreateScope();
        var unchanged = (await verifyScope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
            .ListForUserAsync(userId)).Single();
        Assert.AreEqual(0, unchanged.AdditionOperationHash!.Length);
    }
}
