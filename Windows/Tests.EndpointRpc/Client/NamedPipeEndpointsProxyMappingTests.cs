using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Errors;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Client;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Client;

[TestClass]
public sealed class NamedPipeEndpointsProxyMappingTests
{
    [TestMethod]
    public async Task EveryProxyMethodSendsExactlyItsStableOperationId()
    {
        var transport = new RecordingEndpointRpcTransport((operationId, _, _) =>
        {
            var mutationOutcome = EndpointOperationManifest.Get(operationId).MutatesState
                ? EndpointMutationOutcome.NotCommitted
                : EndpointMutationOutcome.NotApplicable;
            var remoteError = new EndpointRpcError(
                EndpointRpcErrorCode.BackendFailure,
                EndpointFailureKind.Generic,
                "The endpoint operation failed.",
                RequiresProcessRestart: false,
                mutationOutcome);
            return Task.FromException<byte[]>(new EndpointRpcRemoteException(remoteError));
        });
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        var invocations = CreateInvocations(proxy);
        var endpointMethodNames = typeof(IEndpoints).GetMethods()
            .Select(method => method.Name)
            .ToArray();
        var descriptors = EndpointOperationManifest.All;
        var manifestMethodNames = descriptors.Select(descriptor => descriptor.MethodName).ToArray();
        var operationIds = Enum.GetValues<EndpointOperationId>();

        EndpointOperationTestAssertions.AreEquivalent(
            endpointMethodNames,
            manifestMethodNames,
            "EndpointRpc manifest method mappings");
        EndpointOperationTestAssertions.AreEquivalent(
            endpointMethodNames,
            invocations.Select(invocation => invocation.MethodName),
            "Proxy behavior invocation mappings");
        EndpointOperationTestAssertions.AreEquivalent(
            operationIds.Select(operationId => (int)operationId),
            descriptors.Select(descriptor => (int)descriptor.OperationId),
            "Stable endpoint operation IDs in the manifest");

        var descriptorsByMethod = descriptors.ToDictionary(
            descriptor => descriptor.MethodName,
            StringComparer.Ordinal);
        var expectedOperationIds = invocations
            .Select(invocation => descriptorsByMethod[invocation.MethodName])
            .Select(descriptor => descriptor.OperationId)
            .ToArray();

        foreach (var invocation in invocations)
            await Assert.ThrowsExactlyAsync<EndpointFailureException>(invocation.Invoke);

        CollectionAssert.AreEqual(
            expectedOperationIds.Cast<object>().ToArray(),
            transport.Operations.Cast<object>().ToArray(),
            "Each named proxy invocation must send the operation ID assigned by the manifest.");
    }

    [TestMethod]
    public async Task PreSendCancellationTransmitsNothing()
    {
        var transport = new RecordingEndpointRpcTransport((_, _, _) => Task.FromResult(Array.Empty<byte>()));
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            proxy.GetLocalDeviceInfoAsync(cancellationSource.Token));
        Assert.IsEmpty(transport.Operations);
    }

    [TestMethod]
    public async Task SideEffectingCancellationAfterSubmissionReportsUnknownOutcomeWithoutReplay()
    {
        var sends = 0;
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
        {
            sends++;
            return Task.FromException<byte[]>(new OperationCanceledException());
        });
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
            proxy.LogoutAsync(EndpointRpcTestData.Token));
        Assert.AreEqual(1, sends);
    }

    [TestMethod]
    public async Task SideEffectingMalformedResponseReportsUnknownOutcomeWithoutReplay()
    {
        var sends = 0;
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
        {
            sends++;
            return Task.FromResult("{}"u8.ToArray());
        });
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
            proxy.LoginAsync(new LoginRequest
            {
                Username = "ValidUser1",
                Password = EndpointRpcTestData.Password(),
                RememberMe = false
            }));
        Assert.AreEqual(1, sends);
    }

    [TestMethod]
    [DataRow(EndpointRpcErrorCode.Disconnected, true)]
    [DataRow(EndpointRpcErrorCode.ResponsePayloadTooLarge, false)]
    public async Task ConclusiveRemoteFailureKeepsItsExceptionKindWithoutReplay(EndpointRpcErrorCode code, bool disconnected)
    {
        var sends = 0;
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
        {
            sends++;
            return Task.FromException<byte[]>(new EndpointRpcRemoteException(new EndpointRpcError(
                code, EndpointFailureKind.Generic, "The endpoint operation failed.",
                RequiresProcessRestart: false, EndpointMutationOutcome.NotCommitted)));
        });
        await using var proxy = new NamedPipeEndpointsProxy(transport, new EndpointRpcSerializer(), new EndpointRpcContractValidator());

        if (disconnected)
            await Assert.ThrowsExactlyAsync<EndpointRpcDisconnectedException>(() => proxy.LogoutAsync(EndpointRpcTestData.Token));
        else
            await Assert.ThrowsExactlyAsync<EndpointFailureException>(() => proxy.LogoutAsync(EndpointRpcTestData.Token));

        Assert.AreEqual(1, sends);
    }

    [TestMethod]
    public async Task SensitiveCallerBufferIsNotClearedOrIncludedInError()
    {
        var password = EndpointRpcTestData.Password();
        var expected = password.ToArray();
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new EndpointRpcRemoteException(
                new EndpointRpcError(
                    EndpointRpcErrorCode.ValidationFailed,
                    EndpointFailureKind.InvalidInput,
                    "The endpoint input is invalid.",
                    RequiresProcessRestart: false,
                    EndpointMutationOutcome.NotCommitted))));
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        var exception = await Assert.ThrowsExactlyAsync<EndpointFailureException>(() =>
            proxy.DeleteUserAccountAsync(EndpointRpcTestData.Token, password));

        CollectionAssert.AreEqual(expected, password);
        Assert.AreEqual(EndpointFailureKind.InvalidInput, exception.FailureKind);
        Assert.IsFalse(exception.Message.Contains(Convert.ToBase64String(password), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StableFailureKindsCrossTheProxyWithoutRecreatingBackendExceptions()
    {
        var cases = new (EndpointRpcErrorCode Code, EndpointFailureKind Kind, DeviceEnrollmentErrorCode? Enrollment)[]
        {
            (EndpointRpcErrorCode.AuthenticationFailed, EndpointFailureKind.InvalidCredentials, null),
            (EndpointRpcErrorCode.AuthorizationFailed, EndpointFailureKind.InvalidSession, null),
            (EndpointRpcErrorCode.NotFound, EndpointFailureKind.NotFound, null),
            (EndpointRpcErrorCode.Conflict, EndpointFailureKind.DuplicatePasswordName, null),
            (EndpointRpcErrorCode.OperationRejected, EndpointFailureKind.LimitReached, null),
            (EndpointRpcErrorCode.OperationRejected, EndpointFailureKind.DeviceEnrollment,
                DeviceEnrollmentErrorCode.InvalidCode),
            (EndpointRpcErrorCode.BackendFailure, EndpointFailureKind.Generic, null)
        };

        foreach (var (code, kind, enrollment) in cases)
        {
            var remoteError = new EndpointRpcError(
                code,
                kind,
                "A fixed safe endpoint message.",
                RequiresProcessRestart: false,
                EndpointMutationOutcome.NotApplicable,
                enrollment);
            var transport = new RecordingEndpointRpcTransport((_, _, _) =>
                Task.FromException<byte[]>(new EndpointRpcRemoteException(remoteError)));
            await using var proxy = new NamedPipeEndpointsProxy(
                transport,
                new EndpointRpcSerializer(),
                new EndpointRpcContractValidator());

            var exception = await Assert.ThrowsExactlyAsync<EndpointFailureException>(() =>
                proxy.GetLocalDeviceInfoAsync());

            Assert.AreEqual(kind, exception.FailureKind, code.ToString());
            Assert.AreEqual(enrollment, exception.EnrollmentErrorCode, code.ToString());
            Assert.IsInstanceOfType<EndpointRpcRemoteException>(exception.InnerException, code.ToString());
        }
    }

    [TestMethod]
    public async Task InvalidReadOnlyRemoteResponseIsRejectedBeforeFrontendUse()
    {
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromResult("{}"u8.ToArray()));
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        await Assert.ThrowsExactlyAsync<EndpointRpcPayloadException>(() =>
            proxy.GetLocalDeviceInfoAsync());
    }

    [TestMethod]
    public async Task DisconnectedProxyRejectsNewCallsWithoutReplay()
    {
        var sends = 0;
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
        {
            sends++;
            return Task.FromResult(Array.Empty<byte>());
        });
        await transport.DisposeAsync();
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        await Assert.ThrowsExactlyAsync<EndpointRpcDisconnectedException>(() =>
            proxy.GetLocalDeviceInfoAsync());
        Assert.AreEqual(0, sends);
    }

    [TestMethod]
    public async Task PartialCommitMapsToDedicatedClientExceptionWithoutReplay()
    {
        var sends = 0;
        var remoteError = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointFailureKind.DeviceEnrollment,
            "The device addition was committed, but enrollment recovery is required.",
            RequiresProcessRestart: true,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed);
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
        {
            sends++;
            return Task.FromException<byte[]>(new EndpointRpcRemoteException(remoteError));
        });
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        var exception = await Assert.ThrowsExactlyAsync<EndpointOperationPartiallyCommittedException>(() =>
            proxy.AddDeviceByCodeAsync(EndpointRpcTestData.Token, "ABCD2345"));

        Assert.AreEqual(1, sends);
        Assert.AreEqual(EndpointOperationId.AddDeviceByCode, exception.OperationId);
        Assert.AreEqual(EndpointMutationOutcome.PartiallyCommittedRecoveryRequired, exception.MutationOutcome);
        Assert.IsTrue(exception.RequiresRecovery);
        Assert.IsTrue(exception.RequiresProcessRestart);
        Assert.IsFalse(exception.BlindRetryIsSafe);
        var inner = Assert.IsInstanceOfType<EndpointRpcRemoteException>(exception.InnerException);
        Assert.AreEqual(EndpointFailureKind.DeviceEnrollment, inner.Error.FailureKind);
        Assert.AreEqual(DeviceEnrollmentErrorCode.NewDeviceConnectionFailed, inner.Error.EnrollmentErrorCode);
    }

    [TestMethod]
    public async Task GenericPartialCommitMapsWithoutEnrollmentDetails()
    {
        var remoteError = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointFailureKind.Generic,
            "The endpoint operation committed authoritative state, but required follow-up work did not complete.",
            RequiresProcessRestart: false,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired);
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new EndpointRpcRemoteException(remoteError)));
        var proxy = new NamedPipeEndpointsProxy(transport, new EndpointRpcSerializer(), new EndpointRpcContractValidator());
        var request = (PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.ChangeMasterPasswordEndpointRequest)
            EndpointRpcTestData.CreateRequest(EndpointOperationId.ChangeMasterPassword);

        var exception = await Assert.ThrowsExactlyAsync<EndpointOperationPartiallyCommittedException>(() =>
            proxy.ChangeMasterPasswordAsync(request.Request));

        Assert.AreEqual(EndpointOperationId.ChangeMasterPassword, exception.OperationId);
        Assert.IsFalse(exception.BlindRetryIsSafe);
    }

    [TestMethod]
    public async Task MissingEnrollmentErrorCodeMapsToOutcomeUnknown()
    {
        var remoteError = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointFailureKind.DeviceEnrollment,
            "The device addition was committed, but enrollment recovery is required.",
            RequiresProcessRestart: true,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired);
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new EndpointRpcRemoteException(remoteError)));
        var proxy = new NamedPipeEndpointsProxy(transport, new EndpointRpcSerializer(), new EndpointRpcContractValidator());

        var exception = await Assert.ThrowsExactlyAsync<EndpointOperationOutcomeUnknownException>(() =>
            proxy.AddDeviceByCodeAsync(EndpointRpcTestData.Token, "ABCD2345"));

        Assert.AreEqual(EndpointOperationId.AddDeviceByCode, exception.OperationId);
        Assert.IsFalse(exception.RequiresProcessRestart);
    }

    [TestMethod]
    public async Task OrdinaryConflictDoesNotMapToPartialCommitOrOutcomeUnknown()
    {
        var error = new EndpointRpcError(
            EndpointRpcErrorCode.Conflict,
            EndpointFailureKind.Generic,
            "The endpoint operation conflicts with current state.",
            RequiresProcessRestart: false,
            EndpointMutationOutcome.NotCommitted);
        var transport = new RecordingEndpointRpcTransport((_, _, _) =>
            Task.FromException<byte[]>(new EndpointRpcRemoteException(error)));
        var proxy = new NamedPipeEndpointsProxy(
            transport,
            new EndpointRpcSerializer(),
            new EndpointRpcContractValidator());

        var exception = await Assert.ThrowsExactlyAsync<EndpointFailureException>(() =>
            proxy.AddDeviceByCodeAsync(EndpointRpcTestData.Token, "ABCD2345"));

        Assert.AreEqual(EndpointFailureKind.Generic, exception.FailureKind);
        var inner = Assert.IsInstanceOfType<EndpointRpcRemoteException>(exception.InnerException);
        Assert.AreEqual(EndpointRpcErrorCode.Conflict, inner.Error.ErrorCode);
        Assert.AreEqual(EndpointMutationOutcome.NotCommitted, inner.Error.MutationOutcome);
    }

    private static IReadOnlyList<EndpointProxyInvocation> CreateInvocations(NamedPipeEndpointsProxy proxy)
    {
        Func<EndpointOperationId, object> request = EndpointRpcTestData.CreateRequest;
        var registration = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.RegisterEndpointRequest)request(EndpointOperationId.Register)).Request;
        var login = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.LoginEndpointRequest)request(EndpointOperationId.Login)).Request;
        var master = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.ChangeMasterPasswordEndpointRequest)request(EndpointOperationId.ChangeMasterPassword)).Request;
        var profile = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.UpdateUserProfileInfoEndpointRequest)request(EndpointOperationId.UpdateUserProfileInfo)).Request;
        var newPassword = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.AddNewPasswordEndpointRequest)request(EndpointOperationId.AddNewPassword)).Request;
        var updatePassword = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.UpdatePasswordEndpointRequest)request(EndpointOperationId.UpdatePassword)).Request;
        var exportPasswords = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.ExportPasswordsToUserEndpointRequest)request(EndpointOperationId.ExportPasswordsToUser)).Request;
        var colors = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.AddCustomUserColorsEndpointRequest)request(EndpointOperationId.AddCustomUserColors)).Requests;
        var exportColors = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.ExportCustomUserColorsToUserEndpointRequest)request(EndpointOperationId.ExportCustomUserColorsToUser)).Request;
        var updateColor = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.UpdateCustomUserColorEndpointRequest)request(EndpointOperationId.UpdateCustomUserColor)).Request;
        var newTag = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.AddPasswordTagEndpointRequest)request(EndpointOperationId.AddPasswordTag)).Request;
        var exportTags = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.ExportPasswordTagsToUserEndpointRequest)request(EndpointOperationId.ExportPasswordTagsToUser)).Request;
        var updateTag = ((PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests.UpdatePasswordTagEndpointRequest)request(EndpointOperationId.UpdatePasswordTag)).Request;
        var password = EndpointRpcTestData.Password();

        return
        [
            new(nameof(IEndpoints.RegisterAsync), () => proxy.RegisterAsync(registration)),
            new(nameof(IEndpoints.LoginAsync), () => proxy.LoginAsync(login)),
            new(nameof(IEndpoints.RenewAuthSessionAsync), () => proxy.RenewAuthSessionAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.LogoutAsync), () => proxy.LogoutAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.GetAuthSessionStatusAsync), () => proxy.GetAuthSessionStatusAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.ChangeMasterPasswordAsync), () => proxy.ChangeMasterPasswordAsync(master)),
            new(nameof(IEndpoints.GetUserProfileInfoAsync), () => proxy.GetUserProfileInfoAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.DeleteUserAccountAsync), () => proxy.DeleteUserAccountAsync(EndpointRpcTestData.Token, password)),
            new(nameof(IEndpoints.ChangeUsernameAsync), () => proxy.ChangeUsernameAsync(EndpointRpcTestData.Token, "ValidUser2")),
            new(nameof(IEndpoints.UpdateUserProfileInfoAsync), () => proxy.UpdateUserProfileInfoAsync(profile)),
            new(nameof(IEndpoints.GetLocalDeviceInfoAsync), () => proxy.GetLocalDeviceInfoAsync()),
            new(nameof(IEndpoints.GetLocalUserSyncOnAsync), () => proxy.GetLocalUserSyncOnAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.SetLocalUserSyncOnAsync), () => proxy.SetLocalUserSyncOnAsync(EndpointRpcTestData.Token, true)),
            new(nameof(IEndpoints.SetLocalDeviceNameAsync), () => proxy.SetLocalDeviceNameAsync(EndpointRpcTestData.Token, "Desktop")),
            new(nameof(IEndpoints.GetUserDevicesAsync), () => proxy.GetUserDevicesAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.SetUserDeviceNameAsync), () => proxy.SetUserDeviceNameAsync(EndpointRpcTestData.Token, EndpointRpcTestData.ItemId, "Laptop")),
            new(nameof(IEndpoints.SetUserDeviceSyncOnAsync), () => proxy.SetUserDeviceSyncOnAsync(EndpointRpcTestData.Token, EndpointRpcTestData.ItemId, true)),
            new(nameof(IEndpoints.UnblockUserDeviceAsync), () => proxy.UnblockUserDeviceAsync(EndpointRpcTestData.Token, EndpointRpcTestData.ItemId)),
            new(nameof(IEndpoints.DisconnectUserDeviceAsync), () => proxy.DisconnectUserDeviceAsync(EndpointRpcTestData.Token, EndpointRpcTestData.ItemId, password)),
            new(nameof(IEndpoints.StartDeviceEnrollmentAsync), () => proxy.StartDeviceEnrollmentAsync()),
            new(nameof(IEndpoints.GetDeviceEnrollmentStatusAsync), () => proxy.GetDeviceEnrollmentStatusAsync()),
            new(nameof(IEndpoints.CancelDeviceEnrollmentAsync), () => proxy.CancelDeviceEnrollmentAsync()),
            new(nameof(IEndpoints.AddDeviceByCodeAsync), () => proxy.AddDeviceByCodeAsync(EndpointRpcTestData.Token, "ABCD2345")),
            new(nameof(IEndpoints.RestoreRememberedSessionsAsync), () => proxy.RestoreRememberedSessionsAsync()),
            new(nameof(IEndpoints.InitializeRememberMeSessionAsync), () => proxy.InitializeRememberMeSessionAsync(EndpointRpcTestData.ItemId)),
            new(nameof(IEndpoints.SetRememberMeAsync), () => proxy.SetRememberMeAsync(EndpointRpcTestData.Token, true)),
            new(nameof(IEndpoints.GetSavedPasswordsAsync), () => proxy.GetSavedPasswordsAsync(EndpointRpcTestData.Token)),
            new(nameof(IEndpoints.AddNewPasswordAsync), () => proxy.AddNewPasswordAsync(EndpointRpcTestData.Token, newPassword)),
            new(nameof(IEndpoints.RemovePasswordsAsync), () => proxy.RemovePasswordsAsync(EndpointRpcTestData.Token, [EndpointRpcTestData.ItemId])),
            new(nameof(IEndpoints.GetUnsecurePasswordAsync), () => proxy.GetUnsecurePasswordAsync(EndpointRpcTestData.Token, EndpointRpcTestData.ItemId)),
            new(nameof(IEndpoints.UpdatePasswordAsync), () => proxy.UpdatePasswordAsync(EndpointRpcTestData.Token, updatePassword)),
            new(nameof(IEndpoints.ExportPasswordsToUserAsync), () => proxy.ExportPasswordsToUserAsync(EndpointRpcTestData.Token, exportPasswords)),
            new(nameof(IEndpoints.AddCustomUserColorsAsync), () => proxy.AddCustomUserColorsAsync(EndpointRpcTestData.Token, colors)),
            new(nameof(IEndpoints.DeleteCustomUserColorsAsync), () => proxy.DeleteCustomUserColorsAsync(EndpointRpcTestData.Token, [EndpointRpcTestData.ItemId])),
            new(nameof(IEndpoints.ExportCustomUserColorsToUserAsync), () => proxy.ExportCustomUserColorsToUserAsync(EndpointRpcTestData.Token, exportColors)),
            new(nameof(IEndpoints.UpdateCustomUserColorAsync), () => proxy.UpdateCustomUserColorAsync(EndpointRpcTestData.Token, updateColor)),
            new(nameof(IEndpoints.AddPasswordTagAsync), () => proxy.AddPasswordTagAsync(EndpointRpcTestData.Token, newTag)),
            new(nameof(IEndpoints.DeletePasswordTagAsync), () => proxy.DeletePasswordTagAsync(EndpointRpcTestData.Token, EndpointRpcTestData.ItemId)),
            new(nameof(IEndpoints.ExportPasswordTagsToUserAsync), () => proxy.ExportPasswordTagsToUserAsync(EndpointRpcTestData.Token, exportTags)),
            new(nameof(IEndpoints.UpdatePasswordTagAsync), () => proxy.UpdatePasswordTagAsync(EndpointRpcTestData.Token, updateTag)),
            new(nameof(IEndpoints.RunSelfDiagnosticsAndRepairAsync), () => proxy.RunSelfDiagnosticsAndRepairAsync(EndpointRpcTestData.Token))
        ];
    }

    private sealed record EndpointProxyInvocation(string MethodName, Func<Task> Invoke);
}
