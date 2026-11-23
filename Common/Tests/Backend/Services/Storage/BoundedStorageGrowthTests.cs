using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
[TestCategory("StorageBudget")]
public sealed class BoundedStorageGrowthTests
{
    [TestMethod]
    public async Task LocalMutations_EventuallyRefuseGrowth_AndKeepThePreviouslyCommittedVault()
    {
        await using var host = await ProductionSyncTestHost.CreateAsync();
        var token = await host.Endpoints.RegisterAsync(host.CreateRegistrationRequest("capacity-local"));
        await host.Endpoints.GetSavedPasswordsAsync(token);
        var userId = (await host.GetOnlyLoginIdentityAsync()).UserId;
        var factory = host.Services.GetRequiredService<SqliteConnectionFactory>();
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync();
        await host.Services.GetRequiredService<IUserLifecycleCoordinator>().ExecuteAsync(userId, async ct =>
        {
            var used = await ScalarAsync(connection, "SELECT UsedBytes FROM StorageBudgets WHERE Category='Content';");
            await SetBudgetAsync(connection, "Content", used + 1024);
            for (var index = 0; index < 64; index++)
            {
                using var beforeScope = host.Services.CreateScope();
                var before = (await beforeScope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(userId, ct))!;
                var checkpoint = await host.GetOnlyCanonicalCheckpointAsync(ct);
                var rows = await SqliteStorageMeasurement.CaptureAsync(host, ct);
                var passwords = (await host.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Count;
                try
                {
                    await host.Endpoints.AddNewPasswordAsync(token, new NewPasswordRequest
                    {
                        Name = "capacity-entry-" + index,
                        Password = Encoding.UTF8.GetBytes("capacity-test-password-" + index)
                    });
                }
                catch (StorageCapacityExceededException)
                {
                    using var afterScope = host.Services.CreateScope();
                    var after = (await afterScope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsNoTrackingAsync(userId, ct))!;
                    CollectionAssert.AreEqual(before.EncryptedPayload, after.EncryptedPayload);
                    CollectionAssert.AreEqual(before.EncryptedUserPasswordsDataPayload, after.EncryptedUserPasswordsDataPayload);
                    CollectionAssert.AreEqual(before.IntegrityHash, after.IntegrityHash);
                    var retained = await host.GetOnlyCanonicalCheckpointAsync(ct);
                    Assert.IsNotNull(checkpoint); Assert.IsNotNull(retained);
                    Assert.AreEqual(checkpoint.CheckpointSequence, retained.CheckpointSequence);
                    CollectionAssert.AreEqual(checkpoint.Signature, retained.Signature);
                    rows.AssertSameRows(await SqliteStorageMeasurement.CaptureAsync(host, ct));
                    Assert.AreEqual(passwords, (await host.Endpoints.GetSavedPasswordsAsync(token)).Passwords.Count);
                    return;
                }
            }
            Assert.Fail("A reduced finite content budget must eventually refuse a growing live vault.");
        });
    }
}
