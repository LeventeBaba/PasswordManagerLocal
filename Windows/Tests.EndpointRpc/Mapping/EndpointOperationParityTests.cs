using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Mapping;

[TestClass]
public sealed class EndpointOperationParityTests
{
    [TestMethod]
    public void EveryEndpointMethodHasExactlyOneCompleteMapping()
    {
        var methods = typeof(IEndpoints).GetMethods();
        var descriptors = EndpointOperationManifest.All;
        var operationIds = Enum.GetValues<EndpointOperationId>();
        var validator = new EndpointRpcContractValidator();

        EndpointOperationTestAssertions.AreEquivalent(
            methods.Select(method => method.Name),
            descriptors.Select(descriptor => descriptor.MethodName),
            "IEndpoints methods and EndpointRpc manifest entries");
        EndpointOperationTestAssertions.AreEquivalent(
            operationIds.Select(operationId => (int)operationId),
            descriptors.Select(descriptor => (int)descriptor.OperationId),
            "EndpointOperationId values and EndpointRpc manifest entries");
        Assert.IsFalse(operationIds.Any(value => (int)value == 0));

        foreach (var method in methods)
        {
            var matches = descriptors.Where(item => item.MethodName == method.Name).ToArray();
            Assert.HasCount(1, matches, method.Name);
            var parameters = method.GetParameters();
            Assert.IsTrue(parameters.Length > 0, method.Name);
            Assert.AreEqual(typeof(CancellationToken), parameters[^1].ParameterType, method.Name);
            Assert.IsTrue(parameters[^1].HasDefaultValue, method.Name);
        }

        foreach (var descriptor in descriptors)
        {
            Assert.AreEqual(descriptor, EndpointOperationManifest.Get(descriptor.OperationId));
            Assert.IsTrue(Enum.IsDefined(descriptor.OperationId));
            Assert.IsTrue(validator.CanValidateRequest(descriptor.RequestType));
            Assert.IsTrue(validator.CanValidateResponse(descriptor.ResponseType));
            Assert.IsNotNull(EndpointRpcJsonContext.Default.GetTypeInfo(descriptor.RequestType));
            Assert.IsNotNull(EndpointRpcJsonContext.Default.GetTypeInfo(descriptor.ResponseType));
            Assert.IsTrue(descriptor.MaximumRequestPayloadSize > 0);
            Assert.IsTrue(descriptor.MaximumRequestPayloadSize <= EndpointRpcLimits.MaximumRequestPayloadSize);
            Assert.IsTrue(descriptor.MaximumResponsePayloadSize > 0);
            Assert.IsTrue(descriptor.MaximumResponsePayloadSize <= EndpointRpcLimits.MaximumResponsePayloadSize);
            Assert.IsTrue(descriptor.MaximumLogicalResponsePayloadSize >= descriptor.MaximumResponsePayloadSize);
            Assert.IsTrue(descriptor.MaximumLogicalResponsePayloadSize <= EndpointRpcLimits.MaximumLargeResultTotalBytes);
            Assert.IsTrue(Enum.IsDefined(descriptor.CommitModel));
            Assert.AreEqual(
                descriptor.CommitModel != EndpointMutationCommitModel.NoDurableMutation,
                descriptor.MutatesState,
                descriptor.OperationId.ToString());
            Assert.AreEqual(
                descriptor.CommitModel is EndpointMutationCommitModel.CommitThenFollowUp or
                    EndpointMutationCommitModel.MultiStageRecoverableCommit,
                descriptor.CanPartiallyCommit,
                descriptor.OperationId.ToString());
        }
    }

    [TestMethod]
    public void EmptyOperationContractsUseZeroLengthWireBodies()
    {
        var emptyRequestOperations = EndpointOperationManifest.All
            .Where(descriptor => descriptor.RequestType == typeof(EmptyEndpointRequest))
            .Select(descriptor => descriptor.OperationId);
        EndpointOperationTestAssertions.AreEquivalent(
            new[]
            {
                EndpointOperationId.GetLocalDeviceInfo,
                EndpointOperationId.StartDeviceEnrollment,
                EndpointOperationId.GetDeviceEnrollmentStatus,
                EndpointOperationId.CancelDeviceEnrollment,
                EndpointOperationId.RestoreRememberedSessions
            },
            emptyRequestOperations,
            "Operations using the shared empty request contract");

        var emptyResponseOperations = EndpointOperationManifest.All
            .Where(descriptor => descriptor.ResponseType == typeof(EmptyEndpointResponse))
            .Select(descriptor => descriptor.OperationId);
        EndpointOperationTestAssertions.AreEquivalent(
            new[]
            {
                EndpointOperationId.AddCustomUserColors,
                EndpointOperationId.AddDeviceByCode,
                EndpointOperationId.AddNewPassword,
                EndpointOperationId.AddPasswordTag,
                EndpointOperationId.CancelDeviceEnrollment,
                EndpointOperationId.ChangeMasterPassword,
                EndpointOperationId.ChangeUsername,
                EndpointOperationId.DeleteCustomUserColors,
                EndpointOperationId.DeletePasswordTag,
                EndpointOperationId.DeleteUserAccount,
                EndpointOperationId.ExportCustomUserColorsToUser,
                EndpointOperationId.ExportPasswordTagsToUser,
                EndpointOperationId.ExportPasswordsToUser,
                EndpointOperationId.Logout,
                EndpointOperationId.RemovePasswords,
                EndpointOperationId.SetLocalDeviceName,
                EndpointOperationId.SetLocalUserSyncOn,
                EndpointOperationId.SetRememberMe,
                EndpointOperationId.SetUserDeviceName,
                EndpointOperationId.SetUserDeviceSyncOn,
                EndpointOperationId.UnblockUserDevice,
                EndpointOperationId.UpdateCustomUserColor,
                EndpointOperationId.UpdatePassword,
                EndpointOperationId.UpdatePasswordTag,
                EndpointOperationId.UpdateUserProfileInfo
            },
            emptyResponseOperations,
            "Operations using the shared empty response contract");

        var codec = new EndpointRpcMessageCodec(new EndpointRpcSerializer());
        foreach (var operationId in emptyRequestOperations)
        {
            var message = codec.EncodeRequest(operationId, ReadOnlySpan<byte>.Empty);
            var decoded = codec.DecodeRequest(message);

            Assert.AreEqual(sizeof(int), message.Length, operationId.ToString());
            Assert.AreEqual(operationId, decoded.OperationId, operationId.ToString());
            Assert.AreEqual(0, decoded.Payload!.Length, operationId.ToString());
        }

        Assert.AreEqual(0, codec.EncodeSuccess(ReadOnlySpan<byte>.Empty).Length);
    }

    [TestMethod]
    public void RepresentativeCommitModelsRetainMutationAndPartialCommitMeaning()
    {
        AssertCommitModel(EndpointOperationId.Register, EndpointMutationCommitModel.CommitThenFollowUp);
        AssertCommitModel(EndpointOperationId.RestoreRememberedSessions, EndpointMutationCommitModel.MultiStageRecoverableCommit);
        AssertCommitModel(EndpointOperationId.AddDeviceByCode, EndpointMutationCommitModel.MultiStageRecoverableCommit);
        AssertCommitModel(EndpointOperationId.ChangeMasterPassword, EndpointMutationCommitModel.CommitThenFollowUp);
        AssertCommitModel(EndpointOperationId.DisconnectUserDevice, EndpointMutationCommitModel.SingleAtomicCommit);
        AssertCommitModel(EndpointOperationId.InitializeRememberMeSession, EndpointMutationCommitModel.CommitThenFollowUp);
        AssertCommitModel(EndpointOperationId.UpdatePassword, EndpointMutationCommitModel.CommitThenFollowUp);
        AssertCommitModel(EndpointOperationId.ExportPasswordsToUser, EndpointMutationCommitModel.MultiStageRecoverableCommit);
        AssertCommitModel(EndpointOperationId.ExportCustomUserColorsToUser, EndpointMutationCommitModel.MultiStageRecoverableCommit);
        AssertCommitModel(EndpointOperationId.ExportPasswordTagsToUser, EndpointMutationCommitModel.MultiStageRecoverableCommit);
        AssertCommitModel(EndpointOperationId.GetSavedPasswords, EndpointMutationCommitModel.NoDurableMutation);
    }

    [TestMethod]
    public void OnlySavedPasswordsUsesTheBoundedLargeResponseProtocol()
    {
        var largeDescriptors = EndpointOperationManifest.All
            .Where(descriptor => descriptor.SupportsLargeResponse)
            .ToArray();

        Assert.HasCount(1, largeDescriptors);
        Assert.AreEqual(EndpointOperationId.GetSavedPasswords, largeDescriptors[0].OperationId);
        Assert.AreEqual(
            EndpointRpcLimits.MaximumLargeResultTotalBytes,
            largeDescriptors[0].MaximumLogicalResponsePayloadSize);
    }

    private static void AssertCommitModel(
        EndpointOperationId operationId,
        EndpointMutationCommitModel commitModel)
    {
        var descriptor = EndpointOperationManifest.Get(operationId);
        Assert.AreEqual(commitModel, descriptor.CommitModel);
        Assert.AreEqual(
            commitModel != EndpointMutationCommitModel.NoDurableMutation,
            descriptor.MutatesState);
        Assert.AreEqual(
            commitModel is EndpointMutationCommitModel.CommitThenFollowUp or
                EndpointMutationCommitModel.MultiStageRecoverableCommit,
            descriptor.CanPartiallyCommit);
    }
}
