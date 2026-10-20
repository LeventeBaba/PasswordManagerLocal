using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Requests;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.LargeTransfer;
using PasswordManagerLocal.Windows.EndpointRpc.Contracts.Responses;
using PasswordManagerLocal.Windows.EndpointRpc.Serialization;
using PasswordManagerLocal.Windows.Tests.EndpointRpc.Infrastructure;
using PasswordManagerLocal.Windows.EndpointRpc.Validation;

namespace PasswordManagerLocal.Windows.Tests.EndpointRpc.Validation;

[TestClass]
public sealed class EndpointRpcValidationTests
{
    private readonly EndpointRpcContractValidator _validator = new();

    [TestMethod]
    public void MissingIdentifierIsRejectedBeforeDispatch()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.GetSavedPasswords,
                new GetSavedPasswordsEndpointRequest()));
    }

    [TestMethod]
    public void OversizedSensitiveBinaryValueIsRejectedWithoutEchoingIt()
    {
        var secret = Enumerable.Repeat((byte)0x53, EndpointRpcLimits.MaximumSensitiveBinaryFieldSize + 1).ToArray();
        var exception = Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.DeleteUserAccount,
                new DeleteUserAccountEndpointRequest
                {
                    Token = EndpointRpcTestData.Token,
                    Password = secret
                }));
        Assert.IsFalse(exception.Message.Contains(Convert.ToBase64String(secret), StringComparison.Ordinal));
    }

    [TestMethod]
    public void OversizedAndDuplicateCollectionsAreRejected()
    {
        var oversized = Enumerable.Range(0, EndpointRpcLimits.MaximumCollectionItems + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.RemovePasswords,
                new RemovePasswordsEndpointRequest
                {
                    Token = EndpointRpcTestData.Token,
                    PasswordIds = oversized
                }));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.RemovePasswords,
                new RemovePasswordsEndpointRequest
                {
                    Token = EndpointRpcTestData.Token,
                    PasswordIds = [EndpointRpcTestData.ItemId, EndpointRpcTestData.ItemId]
                }));
    }

    [TestMethod]
    public void UndefinedEnumAndNonUtcTimestampAreRejected()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateResponse(
                EndpointOperationId.GetAuthSessionStatus,
                new GetAuthSessionStatusEndpointResponse
                {
                    Status = new AuthSessionStatusResponse
                    {
                        InvalidationReason = (AuthSessionInvalidationReason)int.MaxValue
                    }
                }));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateResponse(
                EndpointOperationId.GetLocalDeviceInfo,
                new GetLocalDeviceInfoEndpointResponse
                {
                    Device = new LocalDeviceInfoResponse
                    {
                        DeviceId = EndpointRpcTestData.ItemId,
                        TlsCertFingerprint = "fingerprint",
                        DeviceType = DeviceType.WindowsPc,
                        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1))
                    }
                }));
    }


    [TestMethod]
    public void MalformedNestedContractsAreRejectedAsPayloadErrors()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.Login,
                new LoginEndpointRequest { Request = null! }));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateResponse(
                EndpointOperationId.GetSavedPasswords,
                new GetSavedPasswordsEndpointResponse
                {
                    Passwords = new SavedPasswordsResponse
                    {
                        Passwords =
                        [
                            new PasswordInfoResponse
                            {
                                Id = EndpointRpcTestData.ItemId,
                                Name = null!,
                                Description = string.Empty,
                                Color = "#FF14B8A6",
                                CreatedAt = DateTime.UtcNow,
                                LastUpdatedAt = DateTime.UtcNow
                            }
                        ]
                    }
                }));
    }

    [TestMethod]
    public void DuplicateResponseIdentifiersAreRejected()
    {
        var duplicate = new UserDeviceInfoResponse
        {
            DeviceId = EndpointRpcTestData.ItemId,
            Name = "Desktop",
            DeviceType = DeviceType.WindowsPc,
            TlsCertFingerprint = "fingerprint",
            LinkedAt = DateTimeOffset.UtcNow
        };
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateResponse(
                EndpointOperationId.GetUserDevices,
                new GetUserDevicesEndpointResponse
                {
                    Devices = [duplicate, duplicate]
                }));
    }

    [TestMethod]
    public void ErrorCodeFailureKindMismatchAndInvalidRestartFlagAreRejected()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.AuthenticationFailed,
                EndpointFailureKind.Generic,
                "Authentication failed.",
                false,
                EndpointMutationOutcome.NotApplicable)));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.BackendFailure,
                EndpointFailureKind.Generic,
                "The endpoint operation failed.",
                true,
                EndpointMutationOutcome.NotApplicable)));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.ValidationFailed,
                EndpointFailureKind.InvalidInput,
                "The endpoint input is invalid.",
                true,
                EndpointMutationOutcome.NotApplicable)));
    }

    [TestMethod]
    public void ErrorPayloadCannotClaimFullCommitSuccess()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(EndpointOperationId.UpdatePassword, new EndpointRpcError(
                EndpointRpcErrorCode.BackendFailure,
                EndpointFailureKind.Generic,
                "The endpoint operation failed.",
                RequiresProcessRestart: false,
                EndpointMutationOutcome.Committed)));
    }

    [TestMethod]
    public void OperationOutcomeUnknownAllowsEitherRestartValue()
    {
        foreach (var requiresProcessRestart in new[] { false, true })
        {
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.OperationOutcomeUnknown,
                EndpointFailureKind.Generic,
                "The endpoint operation may have executed, but its outcome could not be confirmed.",
                requiresProcessRestart,
                EndpointMutationOutcome.OutcomeUnknown));
        }
    }

    [TestMethod]
    public void CorrelationMismatchErrorCannotRequireRestart()
    {
        _validator.Validate(new EndpointRpcError(
            EndpointRpcErrorCode.EndpointCorrelationMismatch,
            EndpointFailureKind.Generic,
            "The endpoint response correlation does not match the request.",
            false,
            EndpointMutationOutcome.NotApplicable));

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.EndpointCorrelationMismatch,
                EndpointFailureKind.Generic,
                "The endpoint response correlation does not match the request.",
                true,
                EndpointMutationOutcome.NotApplicable)));
    }

    [TestMethod]
    public void UndefinedEndpointErrorCodeIsRejected()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                (EndpointRpcErrorCode)int.MaxValue,
                EndpointFailureKind.Generic,
                "The endpoint operation failed.",
                false,
                EndpointMutationOutcome.NotApplicable)));
    }

    [TestMethod]
    public void FailureKindAndEnrollmentCodeMustFormAnAllowedPair()
    {
        var enrollmentError = new EndpointRpcError(
            EndpointRpcErrorCode.OperationRejected,
            EndpointFailureKind.DeviceEnrollment,
            "The device enrollment operation failed.",
            RequiresProcessRestart: false,
            EndpointMutationOutcome.NotApplicable,
            DeviceEnrollmentErrorCode.InvalidCode);
        _validator.Validate(enrollmentError);

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(enrollmentError with
            {
                FailureKind = EndpointFailureKind.Generic
            }));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(enrollmentError with
            {
                EnrollmentErrorCode = null
            }));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(enrollmentError with
            {
                EnrollmentErrorCode = (DeviceEnrollmentErrorCode)int.MaxValue
            }));
    }

    [TestMethod]
    public void PartialCommitUsesCommitModelAndRetainsEnrollmentCodeWithoutRecoveryIdentifiers()
    {
        var enrollmentError = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointFailureKind.DeviceEnrollment,
            "The device addition was committed, but enrollment recovery is required.",
            RequiresProcessRestart: true,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed);
        var genericError = enrollmentError with
        {
            FailureKind = EndpointFailureKind.Generic,
            EnrollmentErrorCode = null
        };

        _validator.Validate(EndpointOperationId.AddDeviceByCode, enrollmentError);
        _validator.Validate(EndpointOperationId.ChangeMasterPassword, genericError);
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(EndpointOperationId.DisconnectUserDevice, genericError));
    }

    [TestMethod]
    public void InvalidFieldCombinationAndOverlongStringAreRejected()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.UpdatePassword,
                new UpdatePasswordEndpointRequest
                {
                    Token = EndpointRpcTestData.Token,
                    Request = new() { Id = EndpointRpcTestData.ItemId }
                }));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.ValidateRequest(
                EndpointOperationId.AddDeviceByCode,
                new AddDeviceByCodeEndpointRequest
                {
                    Token = EndpointRpcTestData.Token,
                    Code = new string('A', EndpointRpcLimits.MaximumEnrollmentCodeLength + 1)
                }));
    }
    [TestMethod]
    public void LargeTransferDescriptorAndChunkBoundsAreValidated()
    {
        var validator = new EndpointLargeResultContractValidator();
        var descriptor = new EndpointLargeResultDescriptor
        {
            TransferId = Guid.NewGuid(),
            OriginalCorrelationId = 10,
            DeclaredTotalLength = EndpointRpcLimits.MaximumLargeResultTotalBytes,
            DeclaredChunkCount = EndpointRpcLimits.MaximumLargeResultChunkCount,
            ChunkSize = EndpointRpcLimits.MaximumLargeResultChunkBytes,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        validator.Validate(descriptor);

        descriptor.DeclaredTotalLength++;
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() => validator.Validate(descriptor));
        descriptor.DeclaredTotalLength = EndpointRpcLimits.MaximumLargeResultTotalBytes;

        var response = new GetEndpointLargeResultChunkResponse
        {
            TransferId = descriptor.TransferId,
            OriginalCorrelationId = descriptor.OriginalCorrelationId,
            ChunkIndex = 0,
            IsFinal = false,
            DeclaredTotalLength = descriptor.DeclaredTotalLength,
            DeclaredChunkCount = descriptor.DeclaredChunkCount,
            ChunkPayload = new byte[EndpointRpcLimits.MaximumLargeResultChunkBytes + 1]
        };
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() => validator.Validate(response));
    }

}
