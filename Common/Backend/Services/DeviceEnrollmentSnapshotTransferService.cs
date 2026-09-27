using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using System.Net;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;
using PasswordManagerLocal.Common.Backend.State;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceEnrollmentSnapshotTransferService : IDeviceEnrollmentSnapshotTransferService
{
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncTransportClientService _syncTransport;
    private readonly IDeviceEnrollmentSnapshotService _snapshotService;

    public DeviceEnrollmentSnapshotTransferService(
        IDeviceIdentityService identity,
        ISyncTransportClientService syncTransport,
        IDeviceEnrollmentSnapshotService snapshotService)
    {
        _identity = identity;
        _syncTransport = syncTransport;
        _snapshotService = snapshotService;
    }

    public async Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> SendAsync(EnrollmentEndpoint endpoint, string sessionId, byte[] secret, byte[] proof, DeviceEnrollmentSnapshot snapshot, CancellationToken ct = default)
    {
        byte[] snapshotBytes;
        try
        {
            UtcDateTimeUtil.NormalizeObjectGraph(snapshot);
            snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                BackendJsonSerializerContext.Default.DeviceEnrollmentSnapshot);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            BackendDebugLog.Error($"Could not serialize enrollment snapshot for {endpoint.Host}:{endpoint.Port}: {ex.Message}", ex);
            return (false, DeviceEnrollmentErrorCode.ProfileDataInvalid, ex.Message);
        }

        BackendDebugLog.Info($"Enrollment snapshot prepared for {endpoint.Host}:{endpoint.Port}. Size={snapshotBytes.Length} bytes.");

        if (snapshotBytes.Length > SyncConstants.MaxDeviceEnrollmentSnapshotBytes)
            return (false, DeviceEnrollmentErrorCode.ProfileDataTooLarge, "The profile data is too large to transfer in one enrollment request.");

        byte[] encryptedSnapshotBytes;
        byte[] snapshotNonce;
        byte[] snapshotTag;
        try
        {
            (encryptedSnapshotBytes, snapshotNonce, snapshotTag) = _snapshotService.Encrypt(
                sessionId,
                secret,
                snapshotBytes,
                _identity.LocalDeviceId.ToString("N"),
                _identity.OriginInstanceId,
                _identity.SignPublicKey,
                _identity.FingerprintHex,
                endpoint.DeviceId,
                endpoint.OriginInstanceId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshotBytes);
        }

        using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transferTimeout.CancelAfter(TimeSpan.FromSeconds(SyncConstants.DeviceEnrollmentTransferTimeoutSeconds));

        try
        {
            BackendDebugLog.Info($"Trying TCP enrollment snapshot transfer to {endpoint.Host}:{endpoint.Port}.");
            var result = await SendEnrollmentSnapshotStreamAsync(endpoint, sessionId, proof, encryptedSnapshotBytes, snapshotNonce, snapshotTag, transferTimeout.Token);

            if (result.Ok)
                BackendDebugLog.Info($"TCP enrollment snapshot transfer to {endpoint.Host}:{endpoint.Port} completed successfully.");
            else
                BackendDebugLog.Error($"TCP enrollment snapshot transfer to {endpoint.Host}:{endpoint.Port} returned {result.ErrorCode}: {result.Error}");

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or InvalidOperationException or CryptographicException or ArgumentException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            BackendDebugLog.Error($"TCP enrollment snapshot transfer to {endpoint.Host}:{endpoint.Port} threw: {ex.Message}", ex);
            return (false, DeviceEnrollmentErrorCode.NewDeviceConnectionFailed, ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedSnapshotBytes);
            CryptographicOperations.ZeroMemory(snapshotNonce);
            CryptographicOperations.ZeroMemory(snapshotTag);
        }
    }


    private async Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> SendEnrollmentSnapshotStreamAsync(EnrollmentEndpoint endpoint, string sessionId, byte[] proof, byte[] snapshotBytes, byte[] snapshotNonce, byte[] snapshotTag, CancellationToken ct)
    {
        var reply = await _syncTransport.CompleteDeviceEnrollmentStreamAsync(
            endpoint.Host,
            endpoint.Port,
            endpoint.TlsCertFingerprint,
            BuildEnrollmentSnapshotChunks(endpoint, sessionId, proof, snapshotBytes, snapshotNonce, snapshotTag),
            ct);

        return ParseEnrollmentReply(reply);
    }


    private async IAsyncEnumerable<CompleteDeviceEnrollmentChunk> BuildEnrollmentSnapshotChunks(EnrollmentEndpoint endpoint, string sessionId, byte[] proof, byte[] snapshotBytes, byte[] snapshotNonce, byte[] snapshotTag)
    {
        var sourceDeviceId = _identity.LocalDeviceId.ToString("N");

        for (var offset = 0; offset < snapshotBytes.Length; offset += SyncConstants.DeviceEnrollmentSnapshotChunkBytes)
        {
            var count = Math.Min(SyncConstants.DeviceEnrollmentSnapshotChunkBytes, snapshotBytes.Length - offset);
            yield return new CompleteDeviceEnrollmentChunk
            {
                SessionId = offset == 0 ? sessionId : string.Empty,
                CodeProof = offset == 0 ? ByteString.CopyFrom(proof) : ByteString.Empty,
                SourceDeviceId = offset == 0 ? sourceDeviceId : string.Empty,
                SourceSignPub = offset == 0 ? ByteString.CopyFrom(_identity.SignPublicKey) : ByteString.Empty,
                SourceTlsCertFingerprint = offset == 0 ? _identity.FingerprintHex : string.Empty,
                SourceOriginInstanceId = offset == 0 ? _identity.OriginInstanceId.ToString("N") : string.Empty,
                TargetDeviceId = offset == 0 ? endpoint.DeviceId.ToString("N") : string.Empty,
                TargetOriginInstanceId = offset == 0 ? endpoint.OriginInstanceId.ToString("N") : string.Empty,
                SnapshotEncryptionVersion = offset == 0 ? SyncConstants.EnrollmentSnapshotEncryptionVersion : 0,
                SnapshotEncryptionNonce = offset == 0 ? ByteString.CopyFrom(snapshotNonce) : ByteString.Empty,
                SnapshotEncryptionTag = offset == 0 ? ByteString.CopyFrom(snapshotTag) : ByteString.Empty,
                SourceDatabaseVersion = offset == 0 ? DatabaseConstants.CurrentDbVersion : 0,
                SnapshotChunk = ByteString.CopyFrom(snapshotBytes, offset, count)
            };

            await Task.Yield();
        }

        if (snapshotBytes.Length == 0)
        {
            yield return new CompleteDeviceEnrollmentChunk
            {
                SessionId = sessionId,
                CodeProof = ByteString.CopyFrom(proof),
                SourceDeviceId = sourceDeviceId,
                SourceSignPub = ByteString.CopyFrom(_identity.SignPublicKey),
                SourceTlsCertFingerprint = _identity.FingerprintHex,
                SourceOriginInstanceId = _identity.OriginInstanceId.ToString("N"),
                TargetDeviceId = endpoint.DeviceId.ToString("N"),
                TargetOriginInstanceId = endpoint.OriginInstanceId.ToString("N"),
                SnapshotEncryptionVersion = SyncConstants.EnrollmentSnapshotEncryptionVersion,
                SnapshotEncryptionNonce = ByteString.CopyFrom(snapshotNonce),
                SnapshotEncryptionTag = ByteString.CopyFrom(snapshotTag),
                SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion,
                SnapshotChunk = ByteString.Empty
            };
        }
    }


    private (bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error) ParseEnrollmentReply(CompleteDeviceEnrollmentReply reply)
    {
        if (reply.Ok)
            return (true, DeviceEnrollmentErrorCode.Unknown, null);

        var errorCode = Enum.TryParse<DeviceEnrollmentErrorCode>(reply.ErrorCode, out var parsedErrorCode)
            ? parsedErrorCode
            : DeviceEnrollmentErrorCode.NewDeviceRejected;

        return (false, errorCode, string.IsNullOrWhiteSpace(reply.Error) ? "The new device rejected the enrollment request." : reply.Error);
    }
}
