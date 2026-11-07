using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using System.Text;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

public sealed partial class RegularSyncEndToEndTests
{
    private const string LifecycleCredentialName = "Original device credential";

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task UsernameChange_OldUsernameFailsAndNewUsernameWorksOnAllThreeDevices()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeThirdMembershipAsync(setup);
        var userId = GetLifecycleUserId(setup);
        var newUsername = setup.Username + "new";
        await setup.NewDevice.Endpoints.LogoutAsync(setup.NewDeviceToken);

        await setup.Original.Endpoints.ChangeUsernameAsync(setup.OriginalToken, newUsername);
        var renamedIdentity = await setup.Original.GetOnlyLoginIdentityAsync();

        foreach (var peer in new[] { setup.Middle, setup.NewDevice })
        {
            await SynchronizeOneWayUntilAsync(
                setup.Original, peer,
                $"device {peer.Identity.LocalDeviceId:N} receives the renamed login identity",
                async () =>
                {
                    var identity = await peer.GetOnlyLoginIdentityAsync();
                    return identity.Status == UserLoginIdentityStatus.Active &&
                        identity.UsernameHash.SequenceEqual(renamedIdentity.UsernameHash) &&
                        identity.UsernameSalt.SequenceEqual(renamedIdentity.UsernameSalt);
                });
        }

        await setup.Original.Endpoints.LogoutAsync(setup.OriginalToken);
        await setup.Middle.Endpoints.LogoutAsync(setup.MiddleToken);
        foreach (var host in setup.Hosts)
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                host.Endpoints.LoginAsync(host.CreateLoginRequest(setup.Username)));
            await AssertLifecycleLoginAsync(host, userId, host.CreateLoginRequest(newUsername));
        }
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task MasterPasswordChange_OldPasswordFailsAndNewPasswordWorksOnAllThreeDevices()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeThirdMembershipAsync(setup);
        var userId = GetLifecycleUserId(setup);
        const string newPassword = "N3wMasterP@ssword1234";
        await setup.NewDevice.Endpoints.LogoutAsync(setup.NewDeviceToken);

        await setup.Original.Endpoints.ChangeMasterPasswordAsync(new MasterPasswordChangeRequest
        {
            Token = setup.OriginalToken,
            Password = setup.Original.CreateLoginRequest(setup.Username).Password,
            NewPassword = Encoding.UTF8.GetBytes(newPassword)
        });

        Guid rotationOperationId;
        using (var scope = setup.Original.Services.CreateScope())
        {
            var operations = scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>();
            rotationOperationId = (await operations.ListForUserAsync(userId))
                .Single(operation => operation.OperationType == UserControlOperationType.KeyEpochReplacement &&
                                     operation.Status == UserControlOperationStatus.Applied)
                .OperationId;
        }

        foreach (var peer in new[] { setup.Middle, setup.NewDevice })
        {
            await SynchronizeOneWayUntilAsync(
                setup.Original, peer,
                $"device {peer.Identity.LocalDeviceId:N} applies the signed password rotation",
                () => HasAppliedLifecycleOperationAsync(peer, rotationOperationId, UserControlOperationType.KeyEpochReplacement));
        }

        await AssertSessionInvalidatedAsync(
            setup.Middle, setup.MiddleToken, AuthSessionInvalidationReason.ProfilePasswordChanged);
        await setup.Original.Endpoints.LogoutAsync(setup.OriginalToken);
        foreach (var host in setup.Hosts)
            await AssertRotatedPasswordLoginAsync(host, userId, setup.Username, newPassword);
    }

    [TestMethod]
    [Timeout(ThreeDeviceTestTimeoutMilliseconds)]
    [TestCategory("Backend")]
    [TestCategory("Integration")]
    [TestCategory("EndToEnd")]
    public async Task AccountDeletion_InvalidatesLocalAndRemoteSessions_AndRelaysToSignedOutDevice()
    {
        await using var setup = await CreateThreeDeviceSetupAsync();
        await SynchronizeThirdMembershipAsync(setup);
        var userId = GetLifecycleUserId(setup);
        await setup.NewDevice.Endpoints.LogoutAsync(setup.NewDeviceToken);

        // Keep the signed-out peer offline until the middle device has applied deletion.
        setup.Original.Transport.Disconnect();
        setup.Original.ConnectTo(setup.Middle);
        setup.Middle.Transport.Disconnect();
        setup.Middle.ConnectTo(setup.Original);
        setup.NewDevice.Transport.Disconnect();

        await Assert.ThrowsExactlyAsync<InvalidInputException>(() =>
            setup.Original.Endpoints.DeleteUserAccountAsync(
                setup.OriginalToken, Encoding.UTF8.GetBytes("WrongMasterPassword123!")));
        Assert.IsNull(await GetLifecycleDeletionBarrierAsync(setup.Original, userId));
        await AssertPasswordSecretAsync(
            setup.Original, setup.OriginalToken, LifecycleCredentialName, Encoding.UTF8.GetBytes("original-device-secret"));

        await setup.Original.Endpoints.DeleteUserAccountAsync(
            setup.OriginalToken, setup.Original.CreateLoginRequest(setup.Username).Password);
        var deletion = await GetLifecycleDeletionBarrierAsync(setup.Original, userId)
            ?? throw new AssertFailedException("Local deletion did not persist its signed deletion barrier.");
        await AssertSessionInvalidatedAsync(
            setup.Original, setup.OriginalToken, AuthSessionInvalidationReason.ProfileRemoved);
        await AssertAccountDeletedAsync(setup.Original, userId, setup.Username, deletion.DeletionOperationId);

        await SynchronizeOneWayUntilAsync(
            setup.Original, setup.Middle,
            "the signed-in peer applies the signed account deletion",
            () => HasAppliedLifecycleOperationAsync(
                setup.Middle, deletion.DeletionOperationId, UserControlOperationType.AccountDeletion));
        await AssertSessionInvalidatedAsync(
            setup.Middle, setup.MiddleToken, AuthSessionInvalidationReason.ProfileRemoved);
        await AssertAccountDeletedAsync(setup.Middle, userId, setup.Username, deletion.DeletionOperationId);
        Assert.IsNull(await GetLifecycleDeletionBarrierAsync(setup.NewDevice, userId));

        setup.Middle.ConnectTo(setup.NewDevice);
        setup.NewDevice.ConnectTo(setup.Middle);
        await SynchronizeOneWayUntilAsync(
            setup.Middle, setup.NewDevice,
            "the deleted middle device relays the deletion to the signed-out peer",
            () => HasAppliedLifecycleOperationAsync(
                setup.NewDevice, deletion.DeletionOperationId, UserControlOperationType.AccountDeletion));
        await AssertAccountDeletedAsync(setup.NewDevice, userId, setup.Username, deletion.DeletionOperationId);

        await using var restarted = await setup.NewDevice.RestartAsync();
        await AssertAccountDeletedAsync(restarted, userId, setup.Username, deletion.DeletionOperationId);
    }

    private async Task SynchronizeThirdMembershipAsync(ThreeDeviceSetup setup)
    {
        // The first reads release the initial-login gates before synchronizing mutations.
        foreach (var host in setup.Hosts)
            await host.Endpoints.GetSavedPasswordsAsync(setup.TokenFor(host));

        await SynchronizeOneWayUntilAsync(
            setup.Middle, setup.Original,
            "the original device receives the third device's membership before the account change",
            () => HasActiveMembershipForDeviceAsync(setup.Original, setup.NewDevice.Identity.LocalDeviceId));

        foreach (var host in setup.Hosts)
            host.EndpointsCache.Clear();
    }

    private static Guid GetLifecycleUserId(ThreeDeviceSetup setup) =>
        setup.Original.Services.GetRequiredService<ITokenService>().GetUidOrThrow(setup.OriginalToken);

    private static async Task AssertLifecycleLoginAsync(ProductionSyncTestHost host, Guid userId, LoginRequest request)
    {
        var token = await host.Endpoints.LoginAsync(request);
        Assert.AreEqual(userId, host.Services.GetRequiredService<ITokenService>().GetUidOrThrow(token));
        Assert.AreEqual(request.Username, (await host.Endpoints.GetUserProfileInfoAsync(token)).Username);
        await AssertPasswordSecretAsync(
            host, token, LifecycleCredentialName, Encoding.UTF8.GetBytes("original-device-secret"));
    }

    private static async Task AssertRotatedPasswordLoginAsync(
        ProductionSyncTestHost host, Guid userId, string username, string newPassword)
    {
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            host.Endpoints.LoginAsync(host.CreateLoginRequest(username)));
        var request = host.CreateLoginRequest(username);
        request.Password = Encoding.UTF8.GetBytes(newPassword);
        await AssertLifecycleLoginAsync(host, userId, request);
    }

    private static async Task AssertSessionInvalidatedAsync(
        ProductionSyncTestHost host, Guid token, AuthSessionInvalidationReason reason)
    {
        var status = await host.Endpoints.GetAuthSessionStatusAsync(token);
        Assert.IsFalse(status.IsAuthenticated);
        Assert.AreEqual(reason, status.InvalidationReason);
        await Assert.ThrowsExactlyAsync<InvalidTokenException>(() => host.Endpoints.GetSavedPasswordsAsync(token));
    }

    private static async Task<bool> HasAppliedLifecycleOperationAsync(
        ProductionSyncTestHost host, Guid operationId, UserControlOperationType type)
    {
        using var scope = host.Services.CreateScope();
        var operation = await scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>()
            .GetByIdAsync(operationId);
        return operation is { Status: UserControlOperationStatus.Applied } && operation.OperationType == type;
    }

    private static async Task<DeletedUserBarrier?> GetLifecycleDeletionBarrierAsync(ProductionSyncTestHost host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>().GetAsync(userId);
    }

    private static async Task AssertAccountDeletedAsync(
        ProductionSyncTestHost host, Guid userId, string username, Guid deletionOperationId)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var barriers = scope.ServiceProvider.GetRequiredService<IDeletedUserBarrierRepository>();
        Assert.IsFalse(await users.ExistsAsync(userId));
        var barrier = await barriers.GetAsync(userId)
            ?? throw new AssertFailedException($"Device {host.Identity.LocalDeviceId:N} has no durable deletion barrier.");
        Assert.AreEqual(deletionOperationId, barrier.DeletionOperationId);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            host.Endpoints.LoginAsync(host.CreateLoginRequest(username)));
    }
}
