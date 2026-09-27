using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Tcp;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Collections.Concurrent;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

public sealed class SyncPeerProtocolHandler
{
    private readonly IServiceProvider _root;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentIncomingDeltaIds = new(StringComparer.Ordinal);

    public SyncPeerProtocolHandler(IServiceProvider root)
    {
        _root = root;
    }




    public async Task<HelloReply> HelloAsync(HelloRequest request, PeerConnectionContext context, CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        Device? remoteDevice = null;

        try
        {
            ValidateIncomingDatabaseVersionForSync(request.DatabaseVersion);
            ValidateIncomingProtocolVersion(request.ProtocolVersion);
            remoteDevice = await ValidateRemoteDeviceAsync(scope.ServiceProvider, context, request.DeviceId, request.SignPub.ToByteArray(), ct);
            var identity = scope.ServiceProvider.GetRequiredService<IDeviceIdentityService>();
            scope.ServiceProvider.GetService<IDevicePresenceRegistry>()?.RefreshAuthenticated(
                remoteDevice.TlsCertFingerprint,
                endpoint: null,
                DevicePresenceObservationSource.IncomingSync);
            BackendDebugLog.Debug(
                $"Authenticated device presence was refreshed through incoming synchronization. RemoteDeviceId={remoteDevice.Id:N}.",
                "Presence");

            return new HelloReply
            {
                Ok = true,
                ProtocolVersion = SyncConstants.SyncProtocolVersion,
                DeviceId = identity.DeviceIdHex,
                SignPub = ByteString.CopyFrom(identity.SignPublicKey),
                SyncAvailable = identity.IsSyncOn
            };
        }
        catch (Exception ex)
        {
            remoteDevice ??= await TryFindRemoteDeviceForInvalidAttemptAsync(scope.ServiceProvider, context, ct);

            if (remoteDevice is not null && !remoteDevice.IsBlocked && ShouldRecordHelloFailure(ex))
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);

            return new HelloReply { Ok = false };
        }
    }


    public async Task<Ack> PushDeltaAsync(IAsyncEnumerable<DeltaChunk> chunks, PeerConnectionContext context, CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        Device? remoteDevice = null;

        try
        {
            if (!context.RemoteDatabaseVersion.HasValue || !context.RemoteProtocolVersion.HasValue)
                throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "Remote database or synchronization protocol version was not received before sync deltas.");

            ValidateIncomingDatabaseVersionForSync(context.RemoteDatabaseVersion.Value);
            ValidateIncomingProtocolVersion(context.RemoteProtocolVersion.Value);
            remoteDevice = await ValidateRemoteDeviceAsync(scope.ServiceProvider, context, null, null, ct);
            var applier = scope.ServiceProvider.GetRequiredService<IIncomingDeltaApplierService>();
            var deviceSecurity = scope.ServiceProvider.GetRequiredService<IDeviceSecurityService>();
            var identity = scope.ServiceProvider.GetRequiredService<IDeviceIdentityService>();

            var lastSyncedTs = 0L;
            var deltaCount = 0;
            var totalPayloadBytes = 0L;
            var seenDeltaIdsInCall = new HashSet<string>(StringComparer.Ordinal);
            var snapshotReceipts = new List<UserSnapshotReceipt>();
            var controlOperationReceipts = new List<UserControlOperationReceipt>();

            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                deltaCount++;
                if (deltaCount > SyncConstants.MaxIncomingDeltaCountPerCall)
                    throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, "Too many deltas in one sync call.");

                var delta = DeltaMapping.FromProto(chunk);
                totalPayloadBytes += delta.Payload.Length;
                if (totalPayloadBytes > SyncConstants.MaxIncomingDeltaTotalBytesPerCall)
                    throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, "Too much delta data in one sync call.");

                ValidateDeltaTransport(delta, remoteDevice, identity.LocalDeviceId);

                var deltaReplayId = BuildDeltaReplayId(delta);
                var isOrdinaryUserSnapshot =
                    delta.Entity.StartsWith("User:Added:", StringComparison.Ordinal) ||
                    delta.Entity.StartsWith("User:Updated:", StringComparison.Ordinal);
                if ((!seenDeltaIdsInCall.Add(deltaReplayId) || IsRecentIncomingDeltaReplay(deltaReplayId)) && !isOrdinaryUserSnapshot)
                {
                    if (delta.Ts > lastSyncedTs)
                        lastSyncedTs = delta.Ts;

                    continue;
                }

                var applyResult = await applier.ApplyAsync(delta, ct);
                RememberIncomingDelta(deltaReplayId);
                if (applyResult.AppliedTimestamp > lastSyncedTs)
                    lastSyncedTs = applyResult.AppliedTimestamp;
                if (applyResult.UserSnapshotReceipt is not null)
                    snapshotReceipts.Add(ToProtoReceipt(applyResult.UserSnapshotReceipt));
                if (applyResult.UserControlOperationReceipt is not null)
                    controlOperationReceipts.Add(ToProtoReceipt(applyResult.UserControlOperationReceipt));
            }

            await deviceSecurity.ResetInvalidIncomingSyncAsync(remoteDevice, ct);

            var ack = new Ack { LastSyncedTs = lastSyncedTs };
            ack.UserSnapshotReceipts.AddRange(snapshotReceipts);
            ack.UserControlOperationReceipts.AddRange(controlOperationReceipts);
            return ack;
        }
        catch (SyncProtocolException ex)
        {
            if (remoteDevice is null)
                remoteDevice = await TryFindRemoteDeviceForInvalidAttemptAsync(scope.ServiceProvider, context, ct);

            if (remoteDevice is not null &&
                !remoteDevice.IsBlocked &&
                IsInvalidIncomingDataStatus(ex.StatusCode) &&
                ShouldRecordHelloFailure(ex))
            {
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            }

            throw;
        }
        catch (InvalidDataException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);

            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, ex.Message);
        }
        catch (SyncRouteDisabledException)
        {
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Synchronization is disabled for this user and device route.");
        }
        catch (UnauthorizedAccessException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);

            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Delta is not authorized.");
        }
    }


    public async Task<UserSnapshotInventoryExchangeReply> ExchangeUserSnapshotInventoryAsync(
        UserSnapshotInventoryExchangeRequest request,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        Device? remoteDevice = null;
        try
        {
            ValidateAuthenticatedSyncContext(context);
            remoteDevice = await ValidateRemoteDeviceAsync(scope.ServiceProvider, context, null, null, ct);
            var antiEntropy = scope.ServiceProvider.GetRequiredService<IUserSnapshotAntiEntropyService>();
            return await antiEntropy.BuildInventoryReplyAsync(remoteDevice.Id, request, ct);
        }
        catch (InvalidDataException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, ex.Message);
        }
    }


    public async Task<IReadOnlyList<NetworkDelta>> RequestUserSnapshotsAsync(
        UserSnapshotRequestBatch request,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        Device? remoteDevice = null;
        try
        {
            ValidateAuthenticatedSyncContext(context);
            if (request.Requests.Count > SyncConstants.MaxUserSnapshotRequestsPerCall)
                throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, "Too many user snapshots were requested.");

            remoteDevice = await ValidateRemoteDeviceAsync(scope.ServiceProvider, context, null, null, ct);
            var antiEntropy = scope.ServiceProvider.GetRequiredService<IUserSnapshotAntiEntropyService>();
            return await antiEntropy.BuildRequestedSnapshotDeltasAsync(remoteDevice.Id, request.Requests, ct);
        }
        catch (InvalidDataException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, ex.Message);
        }
    }


    public async Task<UserControlOperationInventoryExchangeReply> ExchangeUserControlOperationInventoryAsync(
        UserControlOperationInventoryExchangeRequest request,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        Device? remoteDevice = null;
        try
        {
            ValidateAuthenticatedSyncContext(context);
            remoteDevice = await ValidateRemoteDeviceAsync(scope.ServiceProvider, context, null, null, ct);
            var antiEntropy = scope.ServiceProvider.GetRequiredService<IUserControlOperationAntiEntropyService>();
            return await antiEntropy.BuildInventoryReplyAsync(remoteDevice.Id, request, ct);
        }
        catch (InvalidDataException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, ex.Message);
        }
    }

    public async Task<IReadOnlyList<NetworkDelta>> RequestUserControlOperationsAsync(
        UserControlOperationRequestBatch request,
        PeerConnectionContext context,
        CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        Device? remoteDevice = null;
        try
        {
            ValidateAuthenticatedSyncContext(context);
            if (request.Requests.Count > SyncConstants.MaxUserControlRequestsPerCall)
                throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, "Too many control operations were requested.");

            remoteDevice = await ValidateRemoteDeviceAsync(scope.ServiceProvider, context, null, null, ct);
            var antiEntropy = scope.ServiceProvider.GetRequiredService<IUserControlOperationAntiEntropyService>();
            return await antiEntropy.BuildRequestedOperationDeltasAsync(remoteDevice.Id, request.Requests, ct);
        }
        catch (InvalidDataException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (remoteDevice is not null && !remoteDevice.IsBlocked)
                await RecordInvalidAttemptAsync(scope.ServiceProvider, remoteDevice, ex.Message, ct);
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, ex.Message);
        }
    }

    private static void ValidateAuthenticatedSyncContext(PeerConnectionContext context)
    {
        if (!context.SyncHelloAccepted || !context.RemoteDatabaseVersion.HasValue || !context.RemoteProtocolVersion.HasValue)
            throw new SyncProtocolException(SyncProtocolStatusCode.FailedPrecondition, "A successful synchronization hello is required.");

        ValidateIncomingProtocolVersion(context.RemoteProtocolVersion.Value);
        if (!DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(context.RemoteDatabaseVersion.Value))
        {
            throw new SyncProtocolException(
                SyncProtocolStatusCode.FailedPrecondition,
                DatabaseVersionCompatibilityUtil.BuildIncomingDatabaseVersionUnsupportedMessage(context.RemoteDatabaseVersion.Value));
        }
    }


    private static UserSnapshotReceipt ToProtoReceipt(UserSnapshotReceiptResult receipt) =>
        new()
        {
            UserId = receipt.UserId.ToString("N"),
            OriginDeviceId = receipt.OriginDeviceId.ToString("N"),
            OriginInstanceId = receipt.OriginInstanceId.ToString("N"),
            OriginRevision = receipt.OriginRevision,
            SnapshotHash = ByteString.CopyFrom(receipt.SnapshotHash),
            State = (UserSnapshotReceiptStateProto)(int)receipt.State,
            Detail = receipt.Detail ?? string.Empty
        };

    private static UserControlOperationReceipt ToProtoReceipt(UserControlOperationReceiptResult receipt) =>
        new()
        {
            OperationId = receipt.OperationId.ToString("N"),
            UserId = receipt.UserId.ToString("N"),
            OriginDeviceId = receipt.OriginDeviceId.ToString("N"),
            OriginInstanceId = receipt.OriginInstanceId.ToString("N"),
            OriginSequence = receipt.OriginSequence,
            OperationHash = ByteString.CopyFrom(receipt.OperationHash),
            State = (UserControlOperationReceiptStateProto)(int)receipt.State,
            Detail = receipt.Detail ?? string.Empty
        };

    private static void ValidateIncomingProtocolVersion(int protocolVersion)
    {
        if (protocolVersion != SyncConstants.SyncProtocolVersion)
        {
            throw new SyncProtocolException(
                SyncProtocolStatusCode.FailedPrecondition,
                $"Unsupported synchronization protocol version {protocolVersion}. Required version: {SyncConstants.SyncProtocolVersion}.");
        }
    }


    public async Task<GetDeviceEnrollmentInfoReply> GetDeviceEnrollmentInfoAsync(GetDeviceEnrollmentInfoRequest request, CancellationToken ct)
    {
        using var scope = _root.CreateScope();

        try
        {
            BackendDebugLog.Info($"Incoming GetDeviceEnrollmentInfo request. Session={request.SessionId}.");
            if (!DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(request.SourceDatabaseVersion))
            {
                return new GetDeviceEnrollmentInfoReply
                {
                    Ok = false,
                    Error = DatabaseVersionCompatibilityUtil.BuildIncomingDatabaseVersionUnsupportedMessage(request.SourceDatabaseVersion),
                    ErrorCode = DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion.ToString()
                };
            }

            var enrollmentAvailability = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentAvailability>();
            if (!enrollmentAvailability.IsEnrollmentAllowed)
            {
                return new GetDeviceEnrollmentInfoReply
                {
                    Ok = false,
                    Error = "Device enrollment requires an active interactive session.",
                    ErrorCode = DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString()
                };
            }

            var identity = scope.ServiceProvider.GetRequiredService<IDeviceIdentityService>();
            var enrollmentState = scope.ServiceProvider.GetRequiredService<IEnrollmentRuntimeState>();
            if (!identity.IsSyncOn && !enrollmentState.IsActive)
            {
                return new GetDeviceEnrollmentInfoReply
                {
                    Ok = false,
                    Error = "Local synchronization is disabled.",
                    ErrorCode = DeviceEnrollmentErrorCode.SyncDisabled.ToString()
                };
            }

            var enrollment = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>();
            var result = await enrollment.GetIncomingEnrollmentInfoAsync(
                request.SessionId,
                request.CodeProof.ToByteArray(),
                ct);

            return new GetDeviceEnrollmentInfoReply
            {
                Ok = result.Ok,
                Error = result.Error ?? string.Empty,
                ErrorCode = result.ErrorCode.ToString(),
                DeviceId = result.DeviceId == Guid.Empty ? string.Empty : result.DeviceId.ToString("N"),
                TlsCertFingerprint = result.TlsCertFingerprint,
                SignPub = ByteString.CopyFrom(result.SignPublicKey),
                AgreementPub = ByteString.CopyFrom(result.AgreementPublicKey),
                DeviceType = (uint)result.DeviceType,
                OriginInstanceId = result.OriginInstanceId == Guid.Empty ? string.Empty : result.OriginInstanceId.ToString("N")
            };
        }
        catch (DeviceEnrollmentException ex)
        {
            BackendDebugLog.Error($"Incoming GetDeviceEnrollmentInfo request was rejected. Session={request.SessionId}: {ex.Message}", ex);
            return new GetDeviceEnrollmentInfoReply
            {
                Ok = false,
                Error = ex.Message,
                ErrorCode = ex.ErrorCode.ToString()
            };
        }
        catch (Exception ex)
        {
            BackendDebugLog.Error($"Incoming GetDeviceEnrollmentInfo request failed. Session={request.SessionId}: {ex.Message}", ex);
            return new GetDeviceEnrollmentInfoReply
            {
                Ok = false,
                Error = ex.Message,
                ErrorCode = DeviceEnrollmentErrorCode.Unknown.ToString()
            };
        }
    }


    public async Task<CompleteDeviceEnrollmentReply> CompleteDeviceEnrollmentStreamAsync(IAsyncEnumerable<CompleteDeviceEnrollmentChunk> chunks, PeerConnectionContext context, int sourceDatabaseVersion, CancellationToken ct)
    {
        using var scope = _root.CreateScope();
        IDeviceEnrollmentService? enrollment = null;

        try
        {
            BackendDebugLog.Info("Incoming streaming CompleteDeviceEnrollment request started.");

            var enrollmentAvailability = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentAvailability>();
            if (!enrollmentAvailability.IsEnrollmentAllowed)
            {
                return new CompleteDeviceEnrollmentReply
                {
                    Ok = false,
                    Error = "Device enrollment requires an active interactive session.",
                    ErrorCode = DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString()
                };
            }

            enrollment = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>();

            if (!DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(sourceDatabaseVersion))
            {
                return new CompleteDeviceEnrollmentReply
                {
                    Ok = false,
                    Error = DatabaseVersionCompatibilityUtil.BuildIncomingDatabaseVersionUnsupportedMessage(sourceDatabaseVersion),
                    ErrorCode = DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion.ToString()
                };
            }

            var identity = scope.ServiceProvider.GetRequiredService<IDeviceIdentityService>();
            var enrollmentState = scope.ServiceProvider.GetRequiredService<IEnrollmentRuntimeState>();
            if (!identity.IsSyncOn && !enrollmentState.IsActive)
                return new CompleteDeviceEnrollmentReply { Ok = false, Error = "Local synchronization is disabled.", ErrorCode = DeviceEnrollmentErrorCode.SyncDisabled.ToString() };

            if (string.IsNullOrWhiteSpace(context.ClientCertificateFingerprint))
            {
                var error = await enrollment.RegisterIncomingEnrollmentValidationFailureAsync(
                    DeviceEnrollmentErrorCode.NewDeviceRejected,
                    "Client certificate is missing.",
                    ct);
                return new CompleteDeviceEnrollmentReply { Ok = false, Error = error, ErrorCode = DeviceEnrollmentErrorCode.NewDeviceRejected.ToString() };
            }

            string? sessionId = null;
            byte[]? codeProof = null;
            string? sourceDeviceId = null;
            Guid sourceOriginInstanceId = Guid.Empty;
            Guid targetDeviceId = Guid.Empty;
            Guid targetOriginInstanceId = Guid.Empty;
            byte[]? sourceSignPublicKey = null;
            string? sourceTlsCertFingerprint = null;
            var snapshotEncryptionVersion = 0;
            byte[]? snapshotEncryptionNonce = null;
            byte[]? snapshotEncryptionTag = null;
            var chunkSourceDatabaseVersion = 0;
            var totalBytes = 0L;

            CompleteDeviceEnrollmentReply InvalidEnrollmentMetadata(string message) => new()
            {
                Ok = false,
                Error = message,
                ErrorCode = DeviceEnrollmentErrorCode.ProfileDataInvalid.ToString()
            };

            await using var snapshotStream = new MemoryStream();

            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (!enrollmentAvailability.IsEnrollmentAllowed)
                {
                    return new CompleteDeviceEnrollmentReply
                    {
                        Ok = false,
                        Error = "Device enrollment requires an active interactive session.",
                        ErrorCode = DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString()
                    };
                }

                if (!string.IsNullOrWhiteSpace(chunk.SessionId))
                    sessionId = chunk.SessionId;

                if (chunk.CodeProof.Length > 0)
                    codeProof = chunk.CodeProof.ToByteArray();

                if (!string.IsNullOrWhiteSpace(chunk.SourceDeviceId))
                    sourceDeviceId = chunk.SourceDeviceId;

                if (!string.IsNullOrWhiteSpace(chunk.SourceOriginInstanceId))
                {
                    if (!Guid.TryParseExact(chunk.SourceOriginInstanceId, "N", out var parsedSourceOrigin))
                        return InvalidEnrollmentMetadata("The source installation origin is invalid.");
                    if (sourceOriginInstanceId != Guid.Empty && sourceOriginInstanceId != parsedSourceOrigin)
                        return InvalidEnrollmentMetadata("The source installation origin changed during transfer.");
                    sourceOriginInstanceId = parsedSourceOrigin;
                }

                if (!string.IsNullOrWhiteSpace(chunk.TargetDeviceId))
                {
                    if (!Guid.TryParseExact(chunk.TargetDeviceId, "N", out var parsedTargetDevice))
                        return InvalidEnrollmentMetadata("The target device ID is invalid.");
                    if (targetDeviceId != Guid.Empty && targetDeviceId != parsedTargetDevice)
                        return InvalidEnrollmentMetadata("The target device ID changed during transfer.");
                    targetDeviceId = parsedTargetDevice;
                }

                if (!string.IsNullOrWhiteSpace(chunk.TargetOriginInstanceId))
                {
                    if (!Guid.TryParseExact(chunk.TargetOriginInstanceId, "N", out var parsedTargetOrigin))
                        return InvalidEnrollmentMetadata("The target installation origin is invalid.");
                    if (targetOriginInstanceId != Guid.Empty && targetOriginInstanceId != parsedTargetOrigin)
                        return InvalidEnrollmentMetadata("The target installation origin changed during transfer.");
                    targetOriginInstanceId = parsedTargetOrigin;
                }

                if (chunk.SourceSignPub.Length > 0)
                    sourceSignPublicKey = chunk.SourceSignPub.ToByteArray();

                if (!string.IsNullOrWhiteSpace(chunk.SourceTlsCertFingerprint))
                    sourceTlsCertFingerprint = chunk.SourceTlsCertFingerprint;

                if (chunk.SnapshotEncryptionVersion > 0)
                    snapshotEncryptionVersion = chunk.SnapshotEncryptionVersion;

                if (chunk.SnapshotEncryptionNonce.Length > 0)
                    snapshotEncryptionNonce = chunk.SnapshotEncryptionNonce.ToByteArray();

                if (chunk.SnapshotEncryptionTag.Length > 0)
                    snapshotEncryptionTag = chunk.SnapshotEncryptionTag.ToByteArray();

                if (chunk.SourceDatabaseVersion > 0)
                {
                    if (chunkSourceDatabaseVersion > 0 && chunkSourceDatabaseVersion != chunk.SourceDatabaseVersion)
                    {
                        return new CompleteDeviceEnrollmentReply
                        {
                            Ok = false,
                            Error = "The enrollment transfer database version metadata is inconsistent.",
                            ErrorCode = DeviceEnrollmentErrorCode.ProfileDataInvalid.ToString()
                        };
                    }

                    chunkSourceDatabaseVersion = chunk.SourceDatabaseVersion;
                }

                var bytes = chunk.SnapshotChunk.ToByteArray();
                totalBytes += bytes.Length;

                if (totalBytes > SyncConstants.MaxDeviceEnrollmentSnapshotBytes)
                {
                    var error = await enrollment.RegisterIncomingEnrollmentValidationFailureAsync(
                        DeviceEnrollmentErrorCode.ProfileDataTooLarge,
                        "The profile data is too large to transfer in one enrollment request.",
                        ct);
                    return new CompleteDeviceEnrollmentReply
                    {
                        Ok = false,
                        Error = error,
                        ErrorCode = DeviceEnrollmentErrorCode.ProfileDataTooLarge.ToString()
                    };
                }

                if (bytes.Length > 0)
                    await snapshotStream.WriteAsync(bytes, ct);
            }

            if (string.IsNullOrWhiteSpace(sessionId) ||
                codeProof is null ||
                string.IsNullOrWhiteSpace(sourceDeviceId) ||
                sourceOriginInstanceId == Guid.Empty ||
                targetDeviceId == Guid.Empty ||
                targetOriginInstanceId == Guid.Empty ||
                sourceSignPublicKey is null ||
                string.IsNullOrWhiteSpace(sourceTlsCertFingerprint) ||
                chunkSourceDatabaseVersion <= 0)
            {
                var error = await enrollment.RegisterIncomingEnrollmentValidationFailureAsync(
                    DeviceEnrollmentErrorCode.ProfileDataInvalid,
                    "The enrollment transfer metadata is incomplete.",
                    ct);
                return new CompleteDeviceEnrollmentReply
                {
                    Ok = false,
                    Error = error,
                    ErrorCode = DeviceEnrollmentErrorCode.ProfileDataInvalid.ToString()
                };
            }
            if (chunkSourceDatabaseVersion != sourceDatabaseVersion)
            {
                var error = await enrollment.RegisterIncomingEnrollmentValidationFailureAsync(
                    DeviceEnrollmentErrorCode.ProfileDataInvalid,
                    "The enrollment transfer database version does not match the stream header.",
                    ct);
                return new CompleteDeviceEnrollmentReply
                {
                    Ok = false,
                    Error = error,
                    ErrorCode = DeviceEnrollmentErrorCode.ProfileDataInvalid.ToString()
                };
            }

            if (!enrollmentAvailability.IsEnrollmentAllowed)
            {
                return new CompleteDeviceEnrollmentReply
                {
                    Ok = false,
                    Error = "Device enrollment requires an active interactive session.",
                    ErrorCode = DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString()
                };
            }

            var result = await enrollment.CompleteIncomingEnrollmentAsync(
                sessionId,
                codeProof,
                snapshotStream.ToArray(),
                sourceDeviceId,
                sourceOriginInstanceId,
                sourceSignPublicKey,
                sourceTlsCertFingerprint,
                context.ClientCertificateFingerprint,
                context.RemoteIpAddress,
                targetDeviceId,
                targetOriginInstanceId,
                snapshotEncryptionVersion,
                snapshotEncryptionNonce ?? [],
                snapshotEncryptionTag ?? [],
                ct);

            BackendDebugLog.Info($"Incoming streaming CompleteDeviceEnrollment request finished. Ok={result.Ok}, ErrorCode={result.ErrorCode}, Error={result.Error}");

            return new CompleteDeviceEnrollmentReply
            {
                Ok = result.Ok,
                Error = result.Error ?? string.Empty,
                ErrorCode = result.ErrorCode.ToString()
            };
        }
        catch (SyncProtocolException ex) when (ex.StatusCode is SyncProtocolStatusCode.InvalidArgument or SyncProtocolStatusCode.ResourceExhausted)
        {
            var enrollmentAvailability = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentAvailability>();
            if (!enrollmentAvailability.IsEnrollmentAllowed)
            {
                return new CompleteDeviceEnrollmentReply
                {
                    Ok = false,
                    Error = "Device enrollment requires an active interactive session.",
                    ErrorCode = DeviceEnrollmentErrorCode.InteractiveSessionRequired.ToString()
                };
            }

            enrollment ??= scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentService>();
            var errorCode = ex.StatusCode == SyncProtocolStatusCode.ResourceExhausted
                ? DeviceEnrollmentErrorCode.ProfileDataTooLarge
                : DeviceEnrollmentErrorCode.ProfileDataInvalid;
            var error = await enrollment.RegisterIncomingEnrollmentValidationFailureAsync(errorCode, ex.Message, ct);
            BackendDebugLog.Error($"Incoming streaming CompleteDeviceEnrollment request was rejected: {error}", ex);
            return new CompleteDeviceEnrollmentReply
            {
                Ok = false,
                Error = error,
                ErrorCode = errorCode.ToString()
            };
        }
        catch (DeviceEnrollmentException ex)
        {
            BackendDebugLog.Error($"Incoming streaming CompleteDeviceEnrollment request was rejected: {ex.Message}", ex);
            return new CompleteDeviceEnrollmentReply
            {
                Ok = false,
                Error = ex.Message,
                ErrorCode = ex.ErrorCode.ToString()
            };
        }
        catch (Exception ex)
        {
            BackendDebugLog.Error($"Incoming streaming CompleteDeviceEnrollment request failed: {ex.Message}", ex);
            return new CompleteDeviceEnrollmentReply
            {
                Ok = false,
                Error = ex.Message,
                ErrorCode = DeviceEnrollmentErrorCode.Unknown.ToString()
            };
        }
    }


    private void ValidateIncomingDatabaseVersionForSync(int databaseVersion)
    {
        if (DatabaseVersionCompatibilityUtil.IsIncomingDatabaseVersionSupported(databaseVersion))
            return;

        throw new SyncProtocolException(
            SyncProtocolStatusCode.FailedPrecondition,
            DatabaseVersionCompatibilityUtil.BuildIncomingDatabaseVersionUnsupportedMessage(databaseVersion));
    }


    private bool ShouldRecordHelloFailure(Exception exception) =>
        exception is not SyncProtocolException
        {
            StatusCode: SyncProtocolStatusCode.Unavailable
        } &&
        !(exception is SyncProtocolException protocolException &&
          protocolException.StatusCode == SyncProtocolStatusCode.FailedPrecondition &&
          protocolException.Message.Contains("Database version", StringComparison.OrdinalIgnoreCase)) &&
        !(exception is SyncProtocolException protocolException2 &&
          protocolException2.StatusCode == SyncProtocolStatusCode.PermissionDenied &&
          string.Equals(protocolException2.Message, "Remote device is not linked to an enabled local user.", StringComparison.Ordinal));


    private async Task<Device?> TryFindRemoteDeviceForInvalidAttemptAsync(IServiceProvider services, PeerConnectionContext context, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(context.ClientCertificateFingerprint))
                return null;

            var identity = services.GetRequiredService<IDeviceIdentityService>();
            var devices = services.GetRequiredService<IDeviceRepository>();
            var fingerprint = context.ClientCertificateFingerprint;

            if (string.Equals(FingerprintUtil.Normalize(fingerprint), FingerprintUtil.Normalize(identity.FingerprintHex), StringComparison.OrdinalIgnoreCase))
                return null;

            return await devices.GetByTlsCertFingerprintWithUserDevicesAsync(fingerprint, ct);
        }
        catch
        {
            return null;
        }
    }


    private async Task<Device> ValidateRemoteDeviceAsync(
        IServiceProvider services,
        PeerConnectionContext context,
        string? claimedDeviceId,
        byte[]? claimedSignPublicKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.ClientCertificateFingerprint))
            throw new SyncProtocolException(SyncProtocolStatusCode.Unauthenticated, "Client certificate is missing.");

        var identity = services.GetRequiredService<IDeviceIdentityService>();
        if (!identity.IsSyncOn)
            throw new SyncProtocolException(SyncProtocolStatusCode.Unavailable, "Local synchronization is disabled.");

        var devices = services.GetRequiredService<IDeviceRepository>();

        var fingerprint = context.ClientCertificateFingerprint;
        if (string.Equals(FingerprintUtil.Normalize(fingerprint), FingerprintUtil.Normalize(identity.FingerprintHex), StringComparison.OrdinalIgnoreCase))
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Local device cannot sync with itself.");

        var remoteDevice = await devices.GetByTlsCertFingerprintWithUserDevicesAsync(fingerprint, ct);
        if (remoteDevice is null)
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device is not trusted.");

        if (!remoteDevice.IsTrusted)
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device is not allowed to sync.");

        if (remoteDevice.IsBlocked)
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device is not allowed to sync.");

        if (remoteDevice.SignPublicKey.Length == 0)
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device signing key is missing.");

        if (claimedSignPublicKey is not null)
        {
            if (claimedSignPublicKey.Length == 0)
                throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device signing key is missing.");

            if (!remoteDevice.SignPublicKey.SequenceEqual(claimedSignPublicKey))
                throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device signing key does not match.");
        }

        if (claimedDeviceId is not null)
        {
            if (string.IsNullOrWhiteSpace(claimedDeviceId))
                throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device id is missing.");

            if (!string.Equals(claimedDeviceId, BuildDeviceId(remoteDevice.SignPublicKey), StringComparison.OrdinalIgnoreCase))
                throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device id does not match.");
        }

        if (remoteDevice.Id == identity.LocalDeviceId || identity.SignPublicKey.SequenceEqual(remoteDevice.SignPublicKey))
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Local device cannot sync with itself.");

        var routes = services.GetRequiredService<ISyncRouteRepository>();
        if (!await routes.HasEligibleUserForDeviceAsync(remoteDevice.Id, ct))
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Remote device is not linked to an enabled local user.");

        return remoteDevice;
    }


    private void ValidateDeltaTransport(NetworkDelta delta, Device remoteDevice, Guid localDeviceId)
    {
        if (string.IsNullOrWhiteSpace(delta.Entity) || delta.Entity.Length > 256)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta entity is invalid.");

        if (delta.Payload.Length == 0 || delta.Payload.Length > SyncConstants.MaxIncomingDeltaPayloadBytes)
            throw new SyncProtocolException(SyncProtocolStatusCode.ResourceExhausted, "Delta payload size is invalid.");

        if (delta.Ts <= 0)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta timestamp is invalid.");

        var maxFutureTs = DateTimeOffset.UtcNow.AddSeconds(SyncConstants.MaxIncomingDeltaFutureSeconds).ToUnixTimeMilliseconds();
        if (delta.Ts > maxFutureTs)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta timestamp is too far in the future.");

        if (delta.SignPub.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            delta.Sig.Length != SyncConstants.SyncDeltaEd25519SignatureBytes)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta signature is incomplete.");

        if (!remoteDevice.SignPublicKey.SequenceEqual(delta.SignPub))
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Delta signer is not the connected device.");

        if (!string.Equals(delta.DeviceId, BuildDeviceId(delta.SignPub), StringComparison.OrdinalIgnoreCase))
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Delta device id is invalid.");

        if (delta.EncryptionVersion != SyncConstants.SyncDeltaEncryptionVersion)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta encryption version is invalid.");

        if (!Guid.TryParseExact(delta.RecipientDeviceId, "N", out var recipientDeviceId) &&
            !Guid.TryParse(delta.RecipientDeviceId, out recipientDeviceId))
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta recipient is invalid.");

        if (recipientDeviceId != localDeviceId)
            throw new SyncProtocolException(SyncProtocolStatusCode.PermissionDenied, "Delta recipient is not this device.");

        if (delta.EphemeralPublicKey.Length != SyncConstants.SyncDeltaX25519PublicKeyBytes)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta ephemeral key is invalid.");

        if (delta.Nonce.Length != SyncConstants.SyncDeltaNonceBytes)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta nonce is invalid.");

        if (delta.Tag.Length != SyncConstants.SyncDeltaTagBytes)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta authentication tag is invalid.");

        if (delta.PayloadHash.Length != SyncConstants.SyncDeltaPayloadHashBytes)
            throw new SyncProtocolException(SyncProtocolStatusCode.InvalidArgument, "Delta payload hash is invalid.");
    }


    private async Task RecordInvalidAttemptAsync(IServiceProvider services, Device remoteDevice, string reason, CancellationToken ct)
    {
        try
        {
            var deviceSecurity = services.GetRequiredService<IDeviceSecurityService>();
            await deviceSecurity.RecordInvalidIncomingSyncAsync(remoteDevice, reason, ct);
        }
        catch
        {
        }
    }


    private bool IsInvalidIncomingDataStatus(SyncProtocolStatusCode statusCode) =>
        statusCode == SyncProtocolStatusCode.InvalidArgument ||
        statusCode == SyncProtocolStatusCode.PermissionDenied ||
        statusCode == SyncProtocolStatusCode.ResourceExhausted ||
        statusCode == SyncProtocolStatusCode.FailedPrecondition;


    private bool IsRecentIncomingDeltaReplay(string deltaReplayId)
    {
        CleanupRecentIncomingDeltaReplayIds();

        if (!_recentIncomingDeltaIds.TryGetValue(deltaReplayId, out var seenAt))
            return false;

        return DateTimeOffset.UtcNow - seenAt < TimeSpan.FromMinutes(SyncConstants.RecentIncomingDeltaReplayWindowMinutes);
    }


    private void RememberIncomingDelta(string deltaReplayId)
    {
        _recentIncomingDeltaIds[deltaReplayId] = DateTimeOffset.UtcNow;
        CleanupRecentIncomingDeltaReplayIds();
    }


    private void CleanupRecentIncomingDeltaReplayIds()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-SyncConstants.RecentIncomingDeltaReplayWindowMinutes);

        foreach (var item in _recentIncomingDeltaIds)
        {
            if (item.Value < cutoff)
                _recentIncomingDeltaIds.TryRemove(item.Key, out _);
        }

        if (_recentIncomingDeltaIds.Count <= SyncConstants.MaxRecentIncomingDeltaReplayIds)
            return;

        foreach (var item in _recentIncomingDeltaIds.OrderBy(item => item.Value).Take(_recentIncomingDeltaIds.Count - SyncConstants.MaxRecentIncomingDeltaReplayIds))
            _recentIncomingDeltaIds.TryRemove(item.Key, out _);
    }


    private string BuildDeltaReplayId(NetworkDelta delta) =>
        $"{delta.DeviceId}:{delta.Ts}:{Convert.ToHexString(delta.Sig)}";


    private string BuildDeviceId(byte[] signPublicKey) =>
        Convert.ToHexString(Hashing.SHA256Hash(signPublicKey));


}
