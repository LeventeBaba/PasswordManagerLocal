using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Responses;
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
    public void ErrorCodeCategoryMismatchAndInvalidRestartFlagAreRejected()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.AuthenticationFailed,
                EndpointRpcErrorCategory.Internal,
                "Authentication failed.",
                1,
                DateTimeOffset.UtcNow,
                false,
                false,
                EndpointMutationOutcome.NotApplicable,
                Recovery: null)));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.BackendFailure,
                EndpointRpcErrorCategory.Internal,
                "The endpoint operation failed.",
                1,
                DateTimeOffset.UtcNow,
                false,
                true,
                EndpointMutationOutcome.NotApplicable,
                Recovery: null)));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.ValidationFailed,
                EndpointRpcErrorCategory.Validation,
                "The endpoint input is invalid.",
                1,
                DateTimeOffset.UtcNow,
                false,
                true,
                EndpointMutationOutcome.NotApplicable,
                Recovery: null)));
    }

    [TestMethod]
    public void ErrorPayloadCannotClaimFullCommitSuccess()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(EndpointOperationId.UpdatePassword, new EndpointRpcError(
                EndpointRpcErrorCode.BackendFailure,
                EndpointRpcErrorCategory.Internal,
                "The endpoint operation failed.",
                1,
                DateTimeOffset.UtcNow,
                IsRetryable: false,
                RequiresProcessRestart: false,
                EndpointMutationOutcome.Committed,
                Recovery: null)));
    }

    [TestMethod]
    public void OperationOutcomeUnknownAllowsEitherRestartValueWithInternalCategory()
    {
        foreach (var requiresProcessRestart in new[] { false, true })
        {
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.OperationOutcomeUnknown,
                EndpointRpcErrorCategory.Internal,
                "The endpoint operation may have executed, but its outcome could not be confirmed.",
                1,
                DateTimeOffset.UtcNow,
                false,
                requiresProcessRestart,
                EndpointMutationOutcome.OutcomeUnknown,
                Recovery: null));
        }

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.OperationOutcomeUnknown,
                EndpointRpcErrorCategory.Cancellation,
                "The endpoint operation may have executed, but its outcome could not be confirmed.",
                1,
                DateTimeOffset.UtcNow,
                false,
                false,
                EndpointMutationOutcome.OutcomeUnknown,
                Recovery: null)));
    }

    [TestMethod]
    public void CorrelationMismatchErrorRequiresInternalCategoryAndCannotRequireRestart()
    {
        _validator.Validate(new EndpointRpcError(
            EndpointRpcErrorCode.EndpointCorrelationMismatch,
            EndpointRpcErrorCategory.Internal,
            "The endpoint response correlation does not match the request.",
            1,
            DateTimeOffset.UtcNow,
            false,
            false,
            EndpointMutationOutcome.NotApplicable,
            Recovery: null));

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.EndpointCorrelationMismatch,
                EndpointRpcErrorCategory.Internal,
                "The endpoint response correlation does not match the request.",
                1,
                DateTimeOffset.UtcNow,
                false,
                true,
                EndpointMutationOutcome.NotApplicable,
                Recovery: null)));
    }

    [TestMethod]
    public void UndefinedEndpointErrorCodeIsRejected()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                (EndpointRpcErrorCode)int.MaxValue,
                EndpointRpcErrorCategory.Internal,
                "The endpoint operation failed.",
                1,
                DateTimeOffset.UtcNow,
                false,
                false,
                EndpointMutationOutcome.NotApplicable,
                Recovery: null)));
    }


    [TestMethod]
    public void PartialCommitMetadataIsOperationSpecificAndRestartIsIndependent()
    {
        foreach (var requiresProcessRestart in new[] { false, true })
        {
            foreach (var transferPending in new[] { false, true })
            {
                var enrollmentError = new EndpointRpcError(
                    EndpointRpcErrorCode.OperationPartiallyCommitted,
                    EndpointRpcErrorCategory.Recovery,
                    "The device addition was committed, but enrollment recovery is required.",
                    1,
                    DateTimeOffset.UtcNow,
                    IsRetryable: false,
                    RequiresProcessRestart: requiresProcessRestart,
                    EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
                    CreateRecovery() with { TransferPending = transferPending });
                var genericError = enrollmentError with { Recovery = null };

                _validator.Validate(EndpointOperationId.AddDeviceByCode, enrollmentError);
                _validator.Validate(EndpointOperationId.ChangeMasterPassword, genericError);
                Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
                    _validator.Validate(EndpointOperationId.AddDeviceByCode, genericError));
                Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
                    _validator.Validate(EndpointOperationId.ChangeMasterPassword, enrollmentError));
            }
        }
    }

    [TestMethod]
    public void PartialCommitRejectsRetryableOrInvalidEnrollmentMetadata()
    {
        var retryable = new EndpointRpcError(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointRpcErrorCategory.Recovery,
            "The device addition was committed, but enrollment recovery is required.",
            1,
            DateTimeOffset.UtcNow,
            IsRetryable: true,
            RequiresProcessRestart: false,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            CreateRecovery());
        var invalidRecovery = retryable with
        {
            IsRetryable = false,
            Recovery = CreateRecovery() with { EnrollmentCommitId = Guid.Empty }
        };

        Assert.ThrowsExactly<EndpointRpcPayloadException>(() => _validator.Validate(retryable));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(EndpointOperationId.AddDeviceByCode, invalidRecovery));
    }

    [TestMethod]
    public void EnrollmentRecoveryRejectsContradictoryBooleanCombinations()
    {
        var invalidRecoveryStates = new[]
        {
            CreateRecovery() with
            {
                RecoveryAvailable = false,
                TransferPending = true,
                RequiresSignedRemovalToUndo = true
            },
            CreateRecovery() with
            {
                RecoveryAvailable = false,
                TransferPending = false,
                RequiresSignedRemovalToUndo = false
            },
            CreateRecovery() with
            {
                RecoveryAvailable = false,
                TransferPending = false,
                RequiresSignedRemovalToUndo = true
            },
            CreateRecovery() with
            {
                RecoveryAvailable = true,
                TransferPending = true,
                RequiresSignedRemovalToUndo = false
            },
            CreateRecovery() with
            {
                RecoveryAvailable = true,
                TransferPending = false,
                RequiresSignedRemovalToUndo = false
            }
        };

        foreach (var recovery in invalidRecoveryStates)
        {
            Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
                _validator.Validate(
                    EndpointOperationId.AddDeviceByCode,
                    CreatePartialCommitError(recovery)));
        }
    }

    [TestMethod]
    public void EnrollmentRecoveryRejectsMissingIdentifiersAndUndefinedKind()
    {
        var invalidRecoveryStates = new[]
        {
            CreateRecovery() with { EnrollmentCommitId = Guid.Empty },
            CreateRecovery() with { TargetDeviceId = Guid.Empty },
            CreateRecovery() with { TargetOriginInstanceId = Guid.Empty },
            CreateRecovery() with { RecoveryKind = (EndpointRecoveryKind)int.MaxValue }
        };

        foreach (var recovery in invalidRecoveryStates)
        {
            Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
                _validator.Validate(
                    EndpointOperationId.AddDeviceByCode,
                    CreatePartialCommitError(recovery)));
        }
    }

    [TestMethod]
    public void OrdinaryAndUnknownErrorsCannotCarryRecoveryMetadata()
    {
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.Conflict,
                EndpointRpcErrorCategory.Conflict,
                "The endpoint operation conflicts with current state.",
                1,
                DateTimeOffset.UtcNow,
                IsRetryable: false,
                RequiresProcessRestart: false,
                EndpointMutationOutcome.NotCommitted,
                CreateRecovery())));
        Assert.ThrowsExactly<EndpointRpcPayloadException>(() =>
            _validator.Validate(new EndpointRpcError(
                EndpointRpcErrorCode.OperationOutcomeUnknown,
                EndpointRpcErrorCategory.Internal,
                "The endpoint operation may have executed, but its outcome could not be confirmed.",
                1,
                DateTimeOffset.UtcNow,
                IsRetryable: false,
                RequiresProcessRestart: false,
                EndpointMutationOutcome.OutcomeUnknown,
                CreateRecovery())));
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

    private static EndpointRpcError CreatePartialCommitError(EndpointRecoveryMetadata recovery) =>
        new(
            EndpointRpcErrorCode.OperationPartiallyCommitted,
            EndpointRpcErrorCategory.Recovery,
            "The device addition was committed, but enrollment recovery is required.",
            1,
            DateTimeOffset.UtcNow,
            IsRetryable: false,
            RequiresProcessRestart: false,
            EndpointMutationOutcome.PartiallyCommittedRecoveryRequired,
            recovery);

    private static EndpointRecoveryMetadata CreateRecovery() =>
        new(
            EndpointRecoveryKind.DeviceEnrollment,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RecoveryAvailable: true,
            TransferPending: true,
            RequiresSignedRemovalToUndo: true);


}
