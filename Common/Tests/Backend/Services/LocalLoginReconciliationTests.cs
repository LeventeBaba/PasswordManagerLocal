using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Tests.Fakes;
using PasswordManagerLocal.Common.Tests.TestInfrastructure;
using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Services;

[TestClass]
public sealed class LocalLoginReconciliationTests
{
    [TestMethod]
    public async Task Login_DerivesThePasswordKeyAfterPendingReplacementChangesTheSalt()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var registered = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var tokens = services.GetRequiredService<ITokenService>();
        var userId = tokens.GetUidOrThrow(registered);
        using var bundle = await services.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(registered);
        auth.LogoutUser(userId);
        var operation = new UserControlOperation
        {
            OperationId = Guid.NewGuid(), UserId = userId, OperationType = UserControlOperationType.KeyEpochReplacement,
            PreviousKeyEpoch = 1, ResultingKeyEpoch = 2, Status = UserControlOperationStatus.StoredPending
        };
        var operations = services.GetRequiredService<IUserControlOperationRepository>();
        await operations.AddAsync(operation);
        var login = host.CreateValidLoginRequest();
        login.Password = System.Text.Encoding.UTF8.GetBytes("ReplacementPassword123!");
        services.GetRequiredService<FakeUserControlOperationInboxService>().ApplyHandler = async (_, ct) =>
        {
            var user = (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, ct))!;
            user.PasswordSalt = Hashing.GenerateSalt();
            user.KeyEpoch = 2;
            user.SavedKey = null;
            using var replacementKey = EncryptionKey.FromPassword(login.Password, user.PasswordSalt);
            user.EncryptedPayload = await DataCodec.SerializeCompressEncryptAsync(
                bundle.UserData, replacementKey, BackendJsonSerializerContext.Default.UserData, ct: ct);
            var states = services.GetRequiredService<IUserControlStateRepository>();
            var state = (await states.GetAsync(userId, ct))!;
            state.AppliedKeyEpoch = 2;
            states.Update(state);
            await services.GetRequiredService<IUserLoginIdentityProjectionService>().SetCanonicalAsync(user, bundle.GeneralUserData.Version, ct);
            await services.GetRequiredService<IUserDataWriterService>().UpdateUserAsync(user, false, ct);
            operation.Status = UserControlOperationStatus.Applied;
            operations.Update(operation);
            return ControlReceipt(operation, UserControlOperationReceiptState.Applied);
        };

        var loggedIn = await auth.LoginAsync(login);

        MSTestAssert.IsTrue(tokens.Validate(loggedIn));
        using var current = await services.GetRequiredService<IUserDataReaderService>().GetLoadAndVerifyUserDataBundleAsync(loggedIn);
        MSTestAssert.AreEqual(bundle.GeneralUserData.Username, current.GeneralUserData.Username);
    }

    [TestMethod]
    public async Task ReflectedPendingSecurityOperation_IsReconciledBeforeLocalLogin()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var registered = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var tokens = services.GetRequiredService<ITokenService>();
        var user = tokens.GetUidOrThrow(registered);
        auth.LogoutUser(user);
        var operations = services.GetRequiredService<IUserControlOperationRepository>();
        var operation = new UserControlOperation
        {
            OperationId = Guid.NewGuid(), UserId = user,
            OperationType = UserControlOperationType.DeviceAddition, Status = UserControlOperationStatus.StoredPending
        };
        await operations.AddAsync(operation);
        var inbox = services.GetRequiredService<FakeUserControlOperationInboxService>();
        inbox.ApplyHandler = (id, _) =>
        {
            MSTestAssert.AreEqual(operation.OperationId, id);
            operation.Status = UserControlOperationStatus.Applied;
            operations.Update(operation);
            return Task.FromResult(ControlReceipt(operation, UserControlOperationReceiptState.Applied));
        };

        var loggedIn = await auth.LoginAsync(host.CreateValidLoginRequest());

        MSTestAssert.IsTrue(tokens.Validate(loggedIn));
        MSTestAssert.AreEqual(UserControlOperationStatus.Applied, (await operations.GetByIdAsync(operation.OperationId))!.Status);
        MSTestAssert.AreEqual(1, inbox.ApplyCalls.Count);
        MSTestAssert.IsTrue(services.GetRequiredService<UserPostLoginWorkScheduler>().IsInitialReadPending(user));
    }

    [TestMethod]
    public async Task AppliedReceiptWithoutDurableResolution_DoesNotAdmitLogin()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var user = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        auth.LogoutUser(user);
        var operation = new UserControlOperation
        {
            OperationId = Guid.NewGuid(), UserId = user,
            OperationType = UserControlOperationType.KeyEpochReplacement, Status = UserControlOperationStatus.StoredPending
        };
        await services.GetRequiredService<IUserControlOperationRepository>().AddAsync(operation);
        var inbox = services.GetRequiredService<FakeUserControlOperationInboxService>();
        inbox.ApplyHandler = (_, _) => Task.FromResult(ControlReceipt(operation, UserControlOperationReceiptState.Applied));

        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => auth.LoginAsync(host.CreateValidLoginRequest()));

        MSTestAssert.AreEqual(1, inbox.ApplyCalls.Count);
        MSTestAssert.AreEqual(0, services.GetRequiredService<ITokenService>().ListTokensByUid(user).Count);
    }

    [TestMethod]
    public async Task RememberMe_ReconcilesAndReloadsBeforeUnprotectingTheSavedKey()
    {
        var protector = new TestKeyProtector();
        using var host = new BackendTestHost(keyProtector: protector, enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var request = host.CreateValidRegistrationRequest();
        request.RememberMe = true;
        var token = await auth.RegisterAsync(request);
        var userId = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        auth.LogoutUser(userId);
        var operation = new UserControlOperation
        {
            OperationId = Guid.NewGuid(), UserId = userId,
            OperationType = UserControlOperationType.KeyEpochReplacement, Status = UserControlOperationStatus.StoredPending
        };
        var operations = services.GetRequiredService<IUserControlOperationRepository>();
        await operations.AddAsync(operation);
        services.GetRequiredService<FakeUserControlOperationInboxService>().ApplyHandler = async (_, ct) =>
        {
            var current = (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, ct))!;
            current.SavedKey = null; // A key transition cleared Remember Me during prerequisite reconciliation.
            await services.GetRequiredService<IUserDataWriterService>().UpdateSavedKeyOnlyAsync(current, ct);
            operation.Status = UserControlOperationStatus.Applied;
            operations.Update(operation);
            return ControlReceipt(operation, UserControlOperationReceiptState.Applied);
        };

        await MSTestAssert.ThrowsExactlyAsync<InvalidTokenException>(() =>
            services.GetRequiredService<IRememberMeService>().InitializeRememberMeSessionAsync(userId));

        MSTestAssert.IsNull(protector.LastUnprotectedBuffer);
        MSTestAssert.AreEqual(0, services.GetRequiredService<ITokenService>().ListTokensByUid(userId).Count);
    }

    private static UserControlOperationReceiptResult ControlReceipt(UserControlOperation operation, UserControlOperationReceiptState state) =>
        new(operation.OperationId, operation.UserId, operation.OriginDeviceId, operation.OriginInstanceId,
            operation.OriginSequence, operation.OperationHash, state);

    [TestMethod]
    public async Task TransientMergeFailure_PreservesVerifiedSessionAndCanBeRetried()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var token = await services.GetRequiredService<IAuthService>().RegisterAsync(host.CreateValidRegistrationRequest());
        var tokens = services.GetRequiredService<ITokenService>();
        var user = tokens.GetUidOrThrow(token);
        var scheduler = services.GetRequiredService<UserPostLoginWorkScheduler>();
        scheduler.DeferUntilInitialRead(user, 1, 1, token);
        scheduler.ReleaseInitialRead(user, token);
        var work = scheduler.TryTakeReady()!;
        ((FakeUserSnapshotMergeCoordinator)services.GetRequiredService<IUserSnapshotMergeCoordinator>()).Handler =
            (_, _, _) => Task.FromException<bool>(new IOException("Simulated transient storage failure"));
        var worker = new UserPostLoginWorkHostedService(services.GetRequiredService<IServiceScopeFactory>(), scheduler, TimeProvider.System);

        await MSTestAssert.ThrowsExactlyAsync<IOException>(() => worker.ProcessAsync(work, CancellationToken.None));
        scheduler.Complete(work, retry: true);

        MSTestAssert.IsTrue(tokens.Validate(token));
        MSTestAssert.IsTrue(services.GetRequiredService<IDataCachingService>().TryGetUserDataBundle(token, out var cached));
        cached!.Dispose();
        MSTestAssert.IsNull(scheduler.TryTakeReady());
    }

    [TestMethod]
    public async Task ConflictDiscoveredByDeferredMerge_InvalidatesTheLocalSession()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var token = await services.GetRequiredService<IAuthService>().RegisterAsync(host.CreateValidRegistrationRequest());
        var tokens = services.GetRequiredService<ITokenService>();
        var user = tokens.GetUidOrThrow(token);
        var scheduler = services.GetRequiredService<UserPostLoginWorkScheduler>();
        scheduler.DeferUntilInitialRead(user, 1, 1, token);
        scheduler.ReleaseInitialRead(user, token);
        var work = scheduler.TryTakeReady()!;
        var merge = (FakeUserSnapshotMergeCoordinator)services.GetRequiredService<IUserSnapshotMergeCoordinator>();
        merge.Handler = async (_, _, ct) =>
        {
            await services.GetRequiredService<IUserSyncFaultRepository>().AddAsync(new UserSyncFault
            {
                UserId = user, Scope = UserSyncFaultScope.DeterministicItem,
                Kind = UserSyncFaultKind.DeterministicItemConflict, Status = UserSyncHealthStatus.TerminalConflict,
                BlocksLogin = true, BlocksMerge = true
            }, ct);
            return false;
        };
        var worker = new UserPostLoginWorkHostedService(services.GetRequiredService<IServiceScopeFactory>(), scheduler, TimeProvider.System);

        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => worker.ProcessAsync(work, CancellationToken.None));
        scheduler.Complete(work, retry: false);

        MSTestAssert.IsFalse(tokens.Validate(token));
        MSTestAssert.IsFalse(services.GetRequiredService<IDataCachingService>().TryGetUserDataBundle(token, out _));
    }

    [TestMethod]
    public async Task WrongPassword_DoesNotIssueASessionOrAttributeLocalCorruption()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var user = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        auth.LogoutUser(user);
        var login = host.CreateValidLoginRequest();
        login.Password = System.Text.Encoding.UTF8.GetBytes("IncorrectPassword123!");

        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => auth.LoginAsync(login));

        MSTestAssert.AreEqual(0, services.GetRequiredService<ITokenService>().ListTokensByUid(user).Count);
        MSTestAssert.AreEqual(0, (await services.GetRequiredService<IUserSyncFaultRepository>().ListActiveForUserAsync(user)).Count);
        MSTestAssert.IsFalse(services.GetRequiredService<UserPostLoginWorkScheduler>().IsInitialReadPending(user));
    }

    [TestMethod]
    public async Task HealthyLogin_IssuesCachedSessionBeforeMergeAndReconcilesAfterInitialList()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var registered = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var tokens = services.GetRequiredService<ITokenService>();
        var user = tokens.GetUidOrThrow(registered);
        auth.LogoutUser(user);
        var merge = (FakeUserSnapshotMergeCoordinator)services.GetRequiredService<IUserSnapshotMergeCoordinator>();
        var recovery = services.GetRequiredService<FakeUserDataRecoveryCoordinator>();
        recovery.Calls.Clear();
        var before = merge.Calls;

        var token = await auth.LoginAsync(host.CreateValidLoginRequest());

        MSTestAssert.IsTrue(tokens.Validate(token));
        MSTestAssert.AreEqual(before, merge.Calls);
        MSTestAssert.AreEqual(0, recovery.Calls.Count);
        MSTestAssert.IsTrue(services.GetRequiredService<IDataCachingService>().TryGetUserDataBundle(token, out var cached));
        cached!.Dispose();
        var scheduler = services.GetRequiredService<UserPostLoginWorkScheduler>();
        MSTestAssert.IsTrue(scheduler.IsInitialReadPending(user));
        MSTestAssert.IsNull(scheduler.TryTakeReady());

        await services.GetRequiredService<IUserPasswordsService>().GetSavedPasswordsAsync(token);
        merge.Handler = async (userId, key, ct) =>
        {
            var current = (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, ct))!;
            using var changed = await services.GetRequiredService<IUserDataReaderService>().GetAndVerifyUserDataBundleAsync(current, key, ct);
            changed.GeneralUserData.FirstName = "Reconciled profile";
            changed.GeneralUserData.Version = services.GetRequiredService<ISyncVersionClockService>().Next();
            await services.GetRequiredService<IUserDataWriterService>().UpdateUserDataBundleAsync(
                changed, current, key, PasswordManagerLocal.Common.Backend.Models.Encrypted.UserDataBlobKind.General, false, ct);
            return true;
        };
        var work = scheduler.TryTakeReady();
        MSTestAssert.IsNotNull(work);
        var worker = new UserPostLoginWorkHostedService(services.GetRequiredService<IServiceScopeFactory>(), scheduler, TimeProvider.System);
        await worker.ProcessAsync(work, CancellationToken.None);
        scheduler.Complete(work, retry: false);

        MSTestAssert.AreEqual(before + 1, merge.Calls);
        MSTestAssert.AreEqual(UserSyncKeyConfidence.AuthenticatedSession, recovery.Calls.Single().KeyConfidence);
        MSTestAssert.IsTrue(tokens.Validate(token));
        MSTestAssert.IsTrue(services.GetRequiredService<IDataCachingService>().TryGetUserDataBundle(token, out var refreshed));
        using (refreshed) MSTestAssert.AreEqual("Reconciled profile", refreshed!.GeneralUserData.FirstName);
    }

    [TestMethod]
    public async Task RememberMe_UsesTheSameVerifiedLocalFastPathAndCachesTheBundle()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var request = host.CreateValidRegistrationRequest();
        request.RememberMe = true;
        var registered = await auth.RegisterAsync(request);
        var user = services.GetRequiredService<ITokenService>().GetUidOrThrow(registered);
        auth.LogoutUser(user);
        var merge = (FakeUserSnapshotMergeCoordinator)services.GetRequiredService<IUserSnapshotMergeCoordinator>();
        var before = merge.Calls;
        var recovery = services.GetRequiredService<FakeUserDataRecoveryCoordinator>();
        recovery.Calls.Clear();

        var token = await services.GetRequiredService<IRememberMeService>().InitializeRememberMeSessionAsync(user);

        MSTestAssert.AreEqual(before, merge.Calls);
        MSTestAssert.AreEqual(0, recovery.Calls.Count);
        MSTestAssert.IsTrue(services.GetRequiredService<UserPostLoginWorkScheduler>().IsInitialReadPending(user));
        MSTestAssert.IsTrue(services.GetRequiredService<IDataCachingService>().TryGetUserDataBundle(token, out var cached));
        cached!.Dispose();
        auth.Logout(token);
        MSTestAssert.IsNull(services.GetRequiredService<UserPostLoginWorkScheduler>().TryTakeReady());
    }

    [TestMethod]
    public async Task MissingCheckpoint_PreservesSynchronousRecoveryBeforeLogin()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var user = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        auth.LogoutUser(user);
        await services.GetRequiredService<IUserCanonicalHealthService>().DeleteCheckpointAsync(user);
        var recovery = services.GetRequiredService<FakeUserDataRecoveryCoordinator>();
        recovery.Calls.Clear();

        var loggedIn = await auth.LoginAsync(host.CreateValidLoginRequest());

        MSTestAssert.IsTrue(services.GetRequiredService<ITokenService>().Validate(loggedIn));
        MSTestAssert.AreEqual(UserDataRecoveryTrigger.Login, recovery.Calls.Single().Trigger);
        MSTestAssert.IsFalse(services.GetRequiredService<UserPostLoginWorkScheduler>().IsInitialReadPending(user));
    }

    [TestMethod]
    public async Task PendingSecurityOperation_DoesNotAdmitLocalLogin()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var token = await auth.RegisterAsync(host.CreateValidRegistrationRequest());
        var user = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        auth.LogoutUser(user);
        await services.GetRequiredService<IUserControlOperationRepository>().AddAsync(new UserControlOperation
        {
            UserId = user, Status = UserControlOperationStatus.StoredPending,
            OperationType = UserControlOperationType.KeyEpochReplacement
        });

        await MSTestAssert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => auth.LoginAsync(host.CreateValidLoginRequest()));
        MSTestAssert.AreEqual(0, services.GetRequiredService<ITokenService>().ListTokensByUid(user).Count);
    }

    [TestMethod]
    public async Task RememberMe_RejectsRemovedMembershipWithoutErasingTheSavedKey()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var auth = services.GetRequiredService<IAuthService>();
        var request = host.CreateValidRegistrationRequest();
        request.RememberMe = true;
        var token = await auth.RegisterAsync(request);
        var userId = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        auth.LogoutUser(userId);
        var memberships = services.GetRequiredService<IUserMembershipAuthorizationRepository>();
        memberships.DeleteRange(await memberships.ListForUserAsync(userId));

        await MSTestAssert.ThrowsExactlyAsync<InvalidTokenException>(() =>
            services.GetRequiredService<IRememberMeService>().InitializeRememberMeSessionAsync(userId));
        MSTestAssert.IsNotNull((await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId))!.SavedKey);
    }

    [TestMethod]
    public async Task Worker_RejectsRetiredEpochBeforeResolvingKeysOrMerging()
    {
        using var host = new BackendTestHost(enableDeferredLogin: true);
        var services = host.Services;
        var token = await services.GetRequiredService<IAuthService>().RegisterAsync(host.CreateValidRegistrationRequest());
        var user = services.GetRequiredService<ITokenService>().GetUidOrThrow(token);
        var scheduler = services.GetRequiredService<UserPostLoginWorkScheduler>();
        scheduler.DeferUntilInitialRead(user, 999, 1, token);
        scheduler.ReleaseInitialRead(user, token);
        var work = scheduler.TryTakeReady()!;
        var merge = (FakeUserSnapshotMergeCoordinator)services.GetRequiredService<IUserSnapshotMergeCoordinator>();
        var before = merge.Calls;
        var worker = new UserPostLoginWorkHostedService(services.GetRequiredService<IServiceScopeFactory>(), scheduler, TimeProvider.System);

        await worker.ProcessAsync(work, CancellationToken.None);
        scheduler.Complete(work, retry: false);

        MSTestAssert.AreEqual(before, merge.Calls);
    }
}
