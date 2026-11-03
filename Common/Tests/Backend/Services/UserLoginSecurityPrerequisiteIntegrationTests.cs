using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class UserLoginSecurityPrerequisiteIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Integration")]
    public async Task RestartedLogin_ReconcilesSignedAdditionAlreadyReflectedInMembership(bool rememberMe)
    {
        await using var original = await ProductionSyncTestHost.CreateAsync();
        await using var addedDevice = await ProductionSyncTestHost.CreateAsync();
        var username = $"loginprereq{Guid.NewGuid().ToString("N")[..12]}";
        var registration = original.CreateRegistrationRequest(username);
        registration.RememberMe = rememberMe;
        var registered = await original.Endpoints.RegisterAsync(registration);
        var userId = original.Services.GetRequiredService<ITokenService>().GetUidOrThrow(registered);
        Guid operationId;

        using (var scope = original.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var user = (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!;
            var target = addedDevice.Identity;
            var envelope = await services.GetRequiredService<IUserControlOperationWriterService>()
                .CreateAppliedDeviceAdditionAsync(user, new EnrollmentEndpoint
                {
                    Host = "127.0.0.1", Port = 26688, DeviceId = target.LocalDeviceId,
                    OriginInstanceId = target.OriginInstanceId, DeviceType = target.DeviceType,
                    TlsCertFingerprint = target.FingerprintHex,
                    SignPublicKey = target.SignPublicKey.ToArray(), AgreementPublicKey = target.AgreementPublicKey.ToArray()
                });
            operationId = envelope.OperationId;
            var operations = services.GetRequiredService<IUserControlOperationRepository>();
            var stored = (await operations.GetByIdAsync(operationId))!;
            // Simulate a retained pending receipt whose exact signed authorization and
            // canonical membership were already installed by enrollment/catch-up.
            stored.Status = UserControlOperationStatus.StoredPending;
            stored.AppliedAtUtc = null;
            operations.Update(stored);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            Assert.AreEqual(2L, user.MembershipEpoch);
            Assert.AreEqual(operationId, (await services.GetRequiredService<IUserMembershipAuthorizationRepository>()
                .GetActiveAsync(userId, target.LocalDeviceId, target.OriginInstanceId))!.AdditionOperationId);
        }

        await using var restarted = await original.RestartAsync();
        var token = rememberMe
            ? await restarted.Endpoints.InitializeRememberMeSessionAsync(userId)
            : await restarted.Endpoints.LoginAsync(restarted.CreateLoginRequest(username));

        Assert.IsTrue(restarted.Services.GetRequiredService<ITokenService>().Validate(token));
        using (var scope = restarted.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var operation = (await services.GetRequiredService<IUserControlOperationRepository>().GetByIdAsync(operationId))!;
            Assert.AreEqual(UserControlOperationStatus.Applied, operation.Status);
            Assert.IsNotNull(operation.AppliedAtUtc);
            var state = (await services.GetRequiredService<IUserControlStateRepository>().GetAsync(userId))!;
            Assert.IsFalse(state.HasConflict);
            Assert.AreEqual(2L, state.AppliedMembershipEpoch);
        }
    }
}
