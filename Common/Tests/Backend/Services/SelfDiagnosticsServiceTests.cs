using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
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
    public async Task SingleCurrentDevice_UnreferencedLegacyMetadata_DiagnosticsRemovesIt()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var username = "diagnosticorphan" + Guid.NewGuid().ToString("N")[..12];
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));
        var staleDeviceId = Guid.NewGuid();

        using (var scope = host.Services.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IUserDataReaderService>();
            var writer = scope.ServiceProvider.GetRequiredService<IUserDataWriterService>();
            var versionClock = scope.ServiceProvider.GetRequiredService<ISyncVersionClockService>();
            using var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
            var stale = new UserDeviceData
            {
                Id = staleDeviceId,
                Name = "Old disconnected name",
                LinkedAt = DateTimeOffset.UtcNow.AddDays(-5),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-4),
                Version = versionClock.Next()
            };
            stale.GenerateIntegrityHash();
            bundle.UserDevicesData.Devices.Add(stale);
            await writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false);
        }

        var result = await host.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(1, result.RepairedCount);
        CollectionAssert.Contains(result.Findings.ToArray(), "RemovedDeviceMetadataCleaned");

        using (var scope = host.Services.CreateScope())
        {
            using var bundle = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>()
                .GetLoadAndVerifyUserDataBundleAsync(token);
            Assert.IsFalse(bundle.UserDevicesData.Devices.Any(device => device.Id == staleDeviceId));
            Assert.IsTrue(bundle.UserDevicesData.DeletedDevices.Any(device => device.Id == staleDeviceId));
        }

        await host.Endpoints.SetLocalDeviceNameAsync(token, "Old disconnected name");
        var devices = await host.Endpoints.GetUserDevicesAsync(token);
        Assert.AreEqual("Old disconnected name", devices.Single(device => device.IsCurrentDevice).Name);
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task RemovedDeviceMetadata_DiagnosticsRemovesStaleEncryptedRecordAndAllowsNameReuse()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var target = await ProductionSyncTestHost.CreateAsync();
        source.ConnectTo(target);
        target.ConnectTo(source);
        var username = "diagnosticmetadata" + Guid.NewGuid().ToString("N")[..12];
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.EnableSyncAsync(token);
        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        await source.Endpoints.SetUserDeviceNameAsync(token, target.Identity.LocalDeviceId, "Reusable removed name");
        var removal = await source.Endpoints.DisconnectUserDeviceAsync(
            token, target.Identity.LocalDeviceId, Encoding.UTF8.GetBytes("P@ssw0rd12345678"));
        Assert.IsTrue(removal.Removed);

        using (var scope = source.Services.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IUserDataReaderService>();
            var writer = scope.ServiceProvider.GetRequiredService<IUserDataWriterService>();
            var versionClock = scope.ServiceProvider.GetRequiredService<ISyncVersionClockService>();
            using var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
            var stale = new UserDeviceData
            {
                Id = target.Identity.LocalDeviceId,
                Name = "Reusable removed name",
                LinkedAt = DateTimeOffset.UtcNow.AddDays(-2),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                Version = versionClock.Next()
            };
            stale.GenerateIntegrityHash();
            bundle.UserDevicesData.Devices.Add(stale);
            await writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false);
        }

        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.IsTrue(result.RepairedCount >= 1);
        CollectionAssert.Contains(result.Findings.ToArray(), "RemovedDeviceMetadataCleaned");

        using (var scope = source.Services.CreateScope())
        {
            using var bundle = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>()
                .GetLoadAndVerifyUserDataBundleAsync(token);
            Assert.IsFalse(bundle.UserDevicesData.Devices.Any(device => device.Id == target.Identity.LocalDeviceId));
            Assert.IsTrue(bundle.UserDevicesData.DeletedDevices.Any(device => device.Id == target.Identity.LocalDeviceId));
        }

        await source.Endpoints.SetLocalDeviceNameAsync(token, "Reusable removed name");
        var devices = await source.Endpoints.GetUserDevicesAsync(token);
        Assert.AreEqual("Reusable removed name", devices.Single(device => device.IsCurrentDevice).Name);
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

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    public async Task SingleCurrentDevice_LegacyMembershipHistoryReset_PreservesAccountAndAllowsEnrollment()
    {
        await using var source = await ProductionSyncTestHost.CreateAsync();
        await using var target = await ProductionSyncTestHost.CreateAsync();
        source.ConnectTo(target);
        target.ConnectTo(source);
        var username = "diagnosticreset" + Guid.NewGuid().ToString("N")[..12];
        var token = await source.Endpoints.RegisterAsync(source.CreateRegistrationRequest(username));
        await source.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
        {
            Name = "Preserved credential", Description = "Legacy history reset", Color = "#FF123456",
            Password = Encoding.UTF8.GetBytes("PreservedPassword123!"), TagIds = []
        });
        var userId = (await source.GetOnlyCanonicalCheckpointAsync())!.UserId;
        var staleDeviceId = Guid.NewGuid();

        using (var scope = source.Services.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IUserDataReaderService>();
            var writer = scope.ServiceProvider.GetRequiredService<IUserDataWriterService>();
            var versionClock = scope.ServiceProvider.GetRequiredService<ISyncVersionClockService>();
            using var bundle = await reader.GetLoadAndVerifyUserDataBundleAsync(token);
            var stale = new UserDeviceData
            {
                Id = staleDeviceId,
                Name = "Legacy removed device name",
                LinkedAt = DateTimeOffset.UtcNow.AddDays(-3),
                LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                Version = versionClock.Next()
            };
            stale.GenerateIntegrityHash();
            bundle.UserDevicesData.Devices.Add(stale);
            await writer.UpdateUserDataBundleAsync(bundle, token, UserDataBlobKind.Devices, false);
        }

        using (var scope = source.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(row => row.UId == userId);
            var state = await db.UserControlStates.SingleAsync(row => row.UserId == userId);
            var now = DateTimeOffset.UtcNow;
            var staleOriginId = Guid.NewGuid();
            var additionId = Guid.NewGuid();
            var removalId = Guid.NewGuid();

            user.MembershipEpoch = 3;
            user.GenerateIntegrityHash();
            state.AppliedMembershipEpoch = 3;
            state.HasConflict = true;
            state.ConflictReason = "legacy-membership-conflict";
            state.ConflictingOperationId = removalId;
            state.ConflictingOperationHash = new byte[32];

            db.UserMembershipAuthorizations.Add(new UserMembershipAuthorization
            {
                AuthorizationId = Guid.NewGuid(),
                UserId = userId,
                DeviceId = staleDeviceId,
                OriginInstanceId = staleOriginId,
                SignPublicKey = source.Identity.SignPublicKey.ToArray(),
                SignPublicKeyHash = PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(source.Identity.SignPublicKey),
                AgreementPublicKeyHash = PasswordManagerLocal.Common.Backend.Security.Hashing.SHA256Hash(source.Identity.AgreementPublicKey),
                TlsCertFingerprint = source.Identity.FingerprintHex,
                DeviceType = source.Identity.DeviceType,
                StartedMembershipEpoch = 2,
                EndedMembershipEpoch = 3,
                MinimumKeyEpoch = 1,
                MaximumKeyEpoch = 1,
                IsActive = false,
                IsGenesis = false,
                AdditionOperationId = additionId,
                AdditionOperationHash = new byte[32],
                RemovalOperationId = removalId,
                RemovalOperationHash = new byte[32],
                CreatedAtUtc = now.AddMinutes(-2),
                EndedAtUtc = now.AddMinutes(-1),
                Version = 1
            });
            db.UserOriginRemovalCutoffs.Add(new UserOriginRemovalCutoff
            {
                UserId = userId,
                DeviceId = staleDeviceId,
                OriginInstanceId = staleOriginId,
                UserKeyEpoch = 1,
                HighestAcceptedSnapshotRevision = 0,
                HighestAcceptedControlSequence = 0,
                ResultingMembershipEpoch = 3,
                AuthorizationId = Guid.NewGuid(),
                RemovalOperationId = removalId,
                RemovalOperationHash = new byte[32],
                CreatedAtUtc = now.AddMinutes(-1)
            });
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var result = await source.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);
        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(1, result.RepairedCount);
        CollectionAssert.Contains(result.Findings.ToArray(), "SingleDeviceLegacyHistoryReset");

        using (var scope = source.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(row => row.UId == userId);
            Assert.AreEqual(1L, user.MembershipEpoch);
            var authorization = (await scope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .ListForUserAsync(userId)).Single();
            Assert.IsTrue(authorization.IsActive);
            Assert.IsTrue(authorization.IsGenesis);
            Assert.AreEqual(source.Identity.LocalDeviceId, authorization.DeviceId);
            Assert.AreEqual(source.Identity.OriginInstanceId, authorization.OriginInstanceId);
            Assert.IsNull(authorization.AdditionOperationId);
            Assert.IsNull(authorization.RemovalOperationId);
            Assert.AreEqual(0, (await scope.ServiceProvider.GetRequiredService<IUserOriginRemovalCutoffRepository>()
                .ListForUserAsync(userId)).Count);
            Assert.AreEqual(0, (await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
                .ListForUserAsync(userId)).Count);
            var state = await db.UserControlStates.AsNoTracking().SingleAsync(row => row.UserId == userId);
            Assert.IsFalse(state.HasConflict);
            Assert.AreEqual(1L, state.AppliedMembershipEpoch);
            Assert.AreEqual(1L, state.NextOriginSequence);
            using var bundle = await scope.ServiceProvider.GetRequiredService<IUserDataReaderService>()
                .GetLoadAndVerifyUserDataBundleAsync(token);
            Assert.IsFalse(bundle.UserDevicesData.Devices.Any(device => device.Id == staleDeviceId));
            Assert.AreEqual(1, bundle.UserDevicesData.Devices.Count);
            Assert.AreEqual(source.Identity.LocalDeviceId, bundle.UserDevicesData.Devices.Single().Id);
            Assert.AreEqual(0, bundle.UserDevicesData.DeletedDevices.Count);
        }

        Assert.IsTrue((await source.Endpoints.GetSavedPasswordsAsync(token)).Passwords
            .Any(password => password.Name == "Preserved credential"));

        var code = await target.Endpoints.StartDeviceEnrollmentAsync();
        await source.Endpoints.AddDeviceByCodeAsync(token, code.Code);
        using var verifyScope = source.Services.CreateScope();
        Assert.AreEqual(2, (await verifyScope.ServiceProvider.GetRequiredService<IUserMembershipAuthorizationRepository>()
            .ListForUserAsync(userId)).Count(row => row.IsActive));
    }

}
