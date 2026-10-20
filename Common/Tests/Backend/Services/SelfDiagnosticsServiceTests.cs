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
    public async Task HealthyCurrentState_DoesNotRepairAnything()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var username = "diagnostichealthy" + Guid.NewGuid().ToString("N")[..12];
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest(username));

        var result = await host.Endpoints.RunSelfDiagnosticsAndRepairAsync(token);

        Assert.IsTrue(result.Healthy, string.Join(", ", result.Findings));
        Assert.AreEqual(0, result.RepairedCount);
        Assert.AreEqual(0, result.Findings.Count);
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

}
