using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Metadata;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Mapping;

[TestClass]
public sealed class EndpointOperationParityTests
{
    [TestMethod]
    public void EveryEndpointMethodHasExactlyOneCompleteMapping()
    {
        Assert.AreEqual(40, EndpointOperationManifest.All.Count);
        var methods = typeof(IEndpoints).GetMethods();
        var descriptors = EndpointOperationManifest.All;
        var operationIds = Enum.GetValues<EndpointOperationId>();
        var validator = new EndpointRpcContractValidator();

        Assert.HasCount(40, methods);
        Assert.HasCount(methods.Length, descriptors);
        Assert.HasCount(methods.Length, operationIds);
        Assert.HasCount(operationIds.Length, operationIds.Distinct());
        Assert.HasCount(operationIds.Length, descriptors.Select(item => item.OperationId).Distinct());
        CollectionAssert.AreEquivalent(
            operationIds.Cast<object>().ToArray(),
            descriptors.Select(item => (object)item.OperationId).ToArray());
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
            Assert.IsTrue(Enum.IsDefined(descriptor.CancellationClassification));
            Assert.IsTrue(Enum.IsDefined(descriptor.CommitModel));
            Assert.IsTrue(Enum.IsDefined(descriptor.ReplaySafety));
            Assert.IsTrue(Enum.IsDefined(descriptor.RecoveryAction));
        }
    }

    [TestMethod]
    public void EveryOperationHasOneCoherentMutationOutcomePolicy()
    {
        Assert.AreEqual(
            32,
            EndpointOperationManifest.All.Count(descriptor =>
                descriptor.ReplaySafety != EndpointMutationReplaySafety.NotApplicable));
        var descriptors = EndpointOperationManifest.All;
        var readOnly = descriptors.Where(descriptor => !descriptor.MutatesState).ToArray();
        var mutations = descriptors.Where(descriptor => descriptor.MutatesState).ToArray();

        Assert.HasCount(8, readOnly);
        Assert.HasCount(32, mutations);
        Assert.IsTrue(readOnly.All(descriptor =>
            descriptor.CommitModel == EndpointMutationCommitModel.NoDurableMutation &&
            descriptor.ReplaySafety == EndpointMutationReplaySafety.NotApplicable &&
            descriptor.AuthoritativeReadBackOperationId is null &&
            descriptor.RecoveryAction == EndpointMutationRecoveryAction.None));
        Assert.IsTrue(mutations.All(descriptor =>
            descriptor.CommitModel != EndpointMutationCommitModel.NoDurableMutation &&
            descriptor.ReplaySafety != EndpointMutationReplaySafety.NotApplicable &&
            descriptor.RecoveryAction != EndpointMutationRecoveryAction.None));
        Assert.IsTrue(descriptors.All(descriptor =>
            descriptor.CanPartiallyCommit ==
            (descriptor.CommitModel is EndpointMutationCommitModel.CommitThenFollowUp or
                EndpointMutationCommitModel.MultiStageRecoverableCommit)));

        foreach (var descriptor in mutations.Where(item => item.AuthoritativeReadBackOperationId.HasValue))
        {
            var readBack = EndpointOperationManifest.Get(descriptor.AuthoritativeReadBackOperationId!.Value);
            Assert.IsFalse(readBack.MutatesState, descriptor.OperationId.ToString());
        }
    }

    [TestMethod]
    public void EnrollmentAndRepresentativeMutationPoliciesMatchTheirCommitModels()
    {
        AssertPolicy(
            EndpointOperationId.Register,
            EndpointMutationCommitModel.CommitThenFollowUp,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            null,
            EndpointMutationRecoveryAction.ReconnectAndInspectSessionState);
        AssertPolicy(
            EndpointOperationId.RestoreRememberedSessions,
            EndpointMutationCommitModel.MultiStageRecoverableCommit,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            null,
            EndpointMutationRecoveryAction.ReconnectAndInspectSessionState);
        AssertPolicy(
            EndpointOperationId.AddDeviceByCode,
            EndpointMutationCommitModel.MultiStageRecoverableCommit,
            EndpointMutationReplaySafety.RecoveryForSameImmutableTargetOnly,
            EndpointOperationId.GetUserDevices,
            EndpointMutationRecoveryAction.ResumeEnrollmentForSameImmutableTargetOrPerformSignedRemoval);
        AssertPolicy(
            EndpointOperationId.DeleteUserAccount,
            EndpointMutationCommitModel.CommitThenFollowUp,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetAuthSessionStatus,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
        AssertPolicy(
            EndpointOperationId.ChangeMasterPassword,
            EndpointMutationCommitModel.CommitThenFollowUp,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetAuthSessionStatus,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
        AssertPolicy(
            EndpointOperationId.DisconnectUserDevice,
            EndpointMutationCommitModel.SingleAtomicCommit,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetUserDevices,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
        AssertPolicy(
            EndpointOperationId.InitializeRememberMeSession,
            EndpointMutationCommitModel.CommitThenFollowUp,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetAuthSessionStatus,
            EndpointMutationRecoveryAction.ReconnectAndInspectSessionState);
        AssertPolicy(
            EndpointOperationId.UpdatePassword,
            EndpointMutationCommitModel.CommitThenFollowUp,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetSavedPasswords,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
        AssertPolicy(
            EndpointOperationId.ExportPasswordsToUser,
            EndpointMutationCommitModel.MultiStageRecoverableCommit,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetSavedPasswords,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
        AssertPolicy(
            EndpointOperationId.ExportCustomUserColorsToUser,
            EndpointMutationCommitModel.MultiStageRecoverableCommit,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetSavedPasswords,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
        AssertPolicy(
            EndpointOperationId.ExportPasswordTagsToUser,
            EndpointMutationCommitModel.MultiStageRecoverableCommit,
            EndpointMutationReplaySafety.AuthoritativeReadBackRequired,
            EndpointOperationId.GetSavedPasswords,
            EndpointMutationRecoveryAction.InspectAuthoritativeState);
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
        Assert.IsTrue(EndpointOperationManifest.All
            .Where(descriptor => descriptor.OperationId != EndpointOperationId.GetSavedPasswords)
            .All(descriptor => !descriptor.SupportsLargeResponse));
    }

    [TestMethod]
    public void InternalLargeTransferContractsUseSourceGeneratedMetadata()
    {
        var internalTypes = new[]
        {
            typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.EndpointLargeResultDescriptor),
            typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.GetEndpointLargeResultChunkRequest),
            typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.GetEndpointLargeResultChunkResponse),
            typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.ReleaseEndpointLargeResultRequest),
            typeof(PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer.ReleaseEndpointLargeResultResponse)
        };

        foreach (var type in internalTypes)
            Assert.IsNotNull(EndpointRpcJsonContext.Default.GetTypeInfo(type), type.FullName);
    }

    private static void AssertPolicy(
        EndpointOperationId operationId,
        EndpointMutationCommitModel commitModel,
        EndpointMutationReplaySafety replaySafety,
        EndpointOperationId? readBackOperationId,
        EndpointMutationRecoveryAction recoveryAction)
    {
        var descriptor = EndpointOperationManifest.Get(operationId);
        Assert.IsTrue(descriptor.MutatesState);
        Assert.AreEqual(commitModel, descriptor.CommitModel);
        Assert.AreEqual(replaySafety, descriptor.ReplaySafety);
        Assert.AreEqual(readBackOperationId, descriptor.AuthoritativeReadBackOperationId);
        Assert.AreEqual(recoveryAction, descriptor.RecoveryAction);
    }


}
