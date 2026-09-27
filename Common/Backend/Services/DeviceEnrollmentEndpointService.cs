using Google.Protobuf;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;
using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Models.Encrypted;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Text.Json;
using PasswordManagerLocal.Common.Backend.State;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services;

public sealed class DeviceEnrollmentEndpointService : IDeviceEnrollmentEndpointService
{
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncTransportClientService _syncTransport;
    private readonly ILocalNetworkAddressService _networkAddresses;

    public DeviceEnrollmentEndpointService(
        IDeviceIdentityService identity,
        ISyncTransportClientService syncTransport,
        ILocalNetworkAddressService networkAddresses)
    {
        _identity = identity;
        _syncTransport = syncTransport;
        _networkAddresses = networkAddresses;
    }

    public async Task VerifyLocalEnrollmentListenerAsync(string sessionId, byte[] secret, DeviceEnrollmentDirectEndpointInfo endpointInfo, CancellationToken ct = default)
    {
        var hosts = endpointInfo.Hosts
            .Where(host => !string.IsNullOrWhiteSpace(host) && IPAddress.TryParse(host, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (hosts.Count == 0)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.LocalNetworkUnavailable, "No usable local network address was available after checking active interfaces, DNS, and the platform route fallback.");

        var failures = new List<string>();
        var request = new GetDeviceEnrollmentInfoRequest
        {
            SessionId = sessionId,
            CodeProof = ByteString.CopyFrom(DeviceEnrollmentCode.BuildEnrollmentInfoProof(sessionId, secret)),
            SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
        };

        foreach (var host in hosts)
        {
            try
            {
                BackendDebugLog.Info($"Local enrollment listener self-test started for {host}:{endpointInfo.Port}.");
                var reply = await _syncTransport.GetDeviceEnrollmentInfoAsync(
                    host,
                    endpointInfo.Port,
                    endpointInfo.TlsCertFingerprint,
                    request,
                    ct);

                if (reply.Ok &&
                    Guid.TryParse(reply.DeviceId, out var deviceId) &&
                    Guid.TryParse(reply.OriginInstanceId, out var originInstanceId) &&
                    deviceId == _identity.LocalDeviceId &&
                    originInstanceId == _identity.OriginInstanceId &&
                    FingerprintUtil.Normalize(reply.TlsCertFingerprint) == FingerprintUtil.Normalize(_identity.FingerprintHex))
                {
                    BackendDebugLog.Info($"Local enrollment listener self-test succeeded for {host}:{endpointInfo.Port}.");
                    return;
                }

                var error = string.IsNullOrWhiteSpace(reply.Error) ? "The listener returned an invalid local identity." : reply.Error;
                failures.Add($"{host}:{endpointInfo.Port} -> {error}");
                BackendDebugLog.Error($"Local enrollment listener self-test failed for {host}:{endpointInfo.Port}: {error}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or InvalidOperationException or CryptographicException or ArgumentException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                failures.Add($"{host}:{endpointInfo.Port} -> {ex.Message}");
                BackendDebugLog.Error($"Local enrollment listener self-test failed for {host}:{endpointInfo.Port}: {ex.Message}", ex);
            }
        }

        throw new DeviceEnrollmentException(
            DeviceEnrollmentErrorCode.LocalEnrollmentListenerUnavailable,
            $"The local TCP enrollment listener could not complete its own authenticated self-test. {string.Join("; ", failures)}");
    }


    public DeviceEnrollmentDirectEndpointInfo BuildDirectEndpointInfo() =>
        new()
        {
            DeviceId = _identity.LocalDeviceId,
            OriginInstanceId = _identity.OriginInstanceId,
            DeviceType = _identity.DeviceType,
            TlsCertFingerprint = _identity.FingerprintHex,
            SignPublicKey = _identity.SignPublicKey,
            AgreementPublicKey = _identity.AgreementPublicKey,
            Port = SyncPort,
            Hosts = _networkAddresses.GetPreferredLocalHosts()
        };


    public int GetDirectEndpointPriorityForThisDevice(EnrollmentEndpoint endpoint) =>
        _networkAddresses.GetRemoteEndpointPriority(endpoint.Host);


    public EnrollmentEndpoint ToEnrollmentEndpoint(DeviceEnrollmentParsedDirectEndpoint endpoint) =>
        new()
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            DeviceId = endpoint.DeviceId,
            OriginInstanceId = endpoint.OriginInstanceId,
            DeviceType = endpoint.DeviceType,
            TlsCertFingerprint = endpoint.TlsCertFingerprint,
            SignPublicKey = endpoint.SignPublicKey,
            AgreementPublicKey = endpoint.AgreementPublicKey
        };


    public bool IsLocalEndpoint(EnrollmentEndpoint endpoint) =>
        IsLocalDeviceIdentity(endpoint.DeviceId, endpoint.SignPublicKey, endpoint.TlsCertFingerprint);





    public bool IsLocalDeviceIdentity(Guid deviceId, byte[] signPublicKey, string tlsCertFingerprint)
    {
        if (deviceId != Guid.Empty && deviceId == _identity.LocalDeviceId)
            return true;

        if (signPublicKey.Length > 0 && _identity.SignPublicKey.SequenceEqual(signPublicKey))
            return true;

        return FingerprintMatchesLocalDevice(tlsCertFingerprint);
    }


    public bool FingerprintMatchesLocalDevice(string tlsCertFingerprint)
    {
        var remoteFingerprint = FingerprintUtil.Normalize(tlsCertFingerprint);
        var localFingerprint = FingerprintUtil.Normalize(_identity.FingerprintHex);

        if (remoteFingerprint.Length == 0 || localFingerprint.Length == 0)
            return false;

        return localFingerprint.StartsWith(remoteFingerprint, StringComparison.OrdinalIgnoreCase) ||
               remoteFingerprint.StartsWith(localFingerprint, StringComparison.OrdinalIgnoreCase);
    }


    public async Task<EnrollmentEndpoint> ResolveEndpointIdentityAsync(EnrollmentEndpoint endpoint, DeviceEnrollmentParsedCode parsed, CancellationToken ct = default)
    {
        BackendDebugLog.Info($"Fetching and verifying enrollment identity from {endpoint.Host}:{endpoint.Port}.");
        var info = await FetchEnrollmentInfoAsync(endpoint, parsed, ct);
        if (!info.Ok)
        {
            BackendDebugLog.Error($"Fetching enrollment identity from {endpoint.Host}:{endpoint.Port} failed with {info.ErrorCode}: {info.Error}");
            throw new DeviceEnrollmentException(info.ErrorCode, info.Error ?? "The new device did not return its enrollment identity.");
        }

        BackendDebugLog.Info($"Fetched enrollment identity from {endpoint.Host}:{endpoint.Port}. DeviceId={info.DeviceId}, DeviceType={info.DeviceType}.");

        if (info.DeviceId == Guid.Empty || info.OriginInstanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(info.TlsCertFingerprint) ||
            info.SignPublicKey.Length != SyncConstants.SyncDeltaEd25519PublicKeyBytes ||
            info.AgreementPublicKey.Length != SyncConstants.SyncDeltaX25519PublicKeyBytes ||
            !DeviceTypeDetector.IsValid(info.DeviceType))
        {
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceRejected, "The new device returned incomplete or invalid enrollment identity data.");
        }

        if (endpoint.DeviceId != Guid.Empty && endpoint.DeviceId != info.DeviceId)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The enrollment code device id does not match the responding device.");
        if (endpoint.OriginInstanceId != Guid.Empty && endpoint.OriginInstanceId != info.OriginInstanceId)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The enrollment code installation origin does not match the responding device.");
        if (endpoint.DeviceType != default && endpoint.DeviceType != info.DeviceType)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The enrollment code device type does not match the responding device.");

        if (endpoint.SignPublicKey.Length > 0 && !endpoint.SignPublicKey.SequenceEqual(info.SignPublicKey))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The enrollment code signing key does not match the responding device.");

        if (endpoint.AgreementPublicKey.Length > 0 && !endpoint.AgreementPublicKey.SequenceEqual(info.AgreementPublicKey))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The enrollment code agreement key does not match the responding device.");

        var expectedFingerprint = FingerprintUtil.Normalize(endpoint.TlsCertFingerprint);
        var actualFingerprint = FingerprintUtil.Normalize(info.TlsCertFingerprint);
        if (expectedFingerprint.Length > 0 && !actualFingerprint.StartsWith(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The enrollment code TLS fingerprint does not match the responding device.");

        var resolved = new EnrollmentEndpoint
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            DeviceId = info.DeviceId,
            OriginInstanceId = info.OriginInstanceId,
            TlsCertFingerprint = info.TlsCertFingerprint,
            SignPublicKey = info.SignPublicKey,
            AgreementPublicKey = info.AgreementPublicKey,
            DeviceType = info.DeviceType
        };

        if (IsLocalEndpoint(resolved))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceRejected, "The enrollment code belongs to this local device.");

        return resolved;
    }


    private async Task<DeviceEnrollmentInfoResponse> FetchEnrollmentInfoAsync(EnrollmentEndpoint endpoint, DeviceEnrollmentParsedCode parsed, CancellationToken ct)
    {
        try
        {
            var reply = await _syncTransport.GetDeviceEnrollmentInfoAsync(endpoint.Host, endpoint.Port, endpoint.TlsCertFingerprint, new GetDeviceEnrollmentInfoRequest
            {
                SessionId = parsed.SessionId,
                CodeProof = ByteString.CopyFrom(DeviceEnrollmentCode.BuildEnrollmentInfoProof(parsed.SessionId, parsed.Secret)),
                SourceDatabaseVersion = DatabaseConstants.CurrentDbVersion
            }, ct);

            var errorCode = Enum.TryParse<DeviceEnrollmentErrorCode>(reply.ErrorCode, out var parsedErrorCode)
                ? parsedErrorCode
                : DeviceEnrollmentErrorCode.NewDeviceRejected;

            if (!reply.Ok)
            {
                return new DeviceEnrollmentInfoResponse
                {
                    Ok = false,
                    ErrorCode = errorCode,
                    Error = string.IsNullOrWhiteSpace(reply.Error) ? "The new device rejected the enrollment identity request." : reply.Error
                };
            }

            if (!Guid.TryParseExact(reply.DeviceId, "N", out var deviceId) && !Guid.TryParse(reply.DeviceId, out deviceId))
            {
                return new DeviceEnrollmentInfoResponse
                {
                    Ok = false,
                    ErrorCode = DeviceEnrollmentErrorCode.NewDeviceRejected,
                    Error = "The new device returned an invalid device id."
                };
            }

            if ((!Guid.TryParseExact(reply.OriginInstanceId, "N", out var originInstanceId) && !Guid.TryParse(reply.OriginInstanceId, out originInstanceId)) || originInstanceId == Guid.Empty)
            {
                return new DeviceEnrollmentInfoResponse
                {
                    Ok = false,
                    ErrorCode = DeviceEnrollmentErrorCode.NewDeviceRejected,
                    Error = "The new device returned an invalid installation origin id."
                };
            }

            if (reply.DeviceType > byte.MaxValue || !DeviceTypeDetector.IsValid((DeviceType)(byte)reply.DeviceType))
            {
                return new DeviceEnrollmentInfoResponse
                {
                    Ok = false,
                    ErrorCode = DeviceEnrollmentErrorCode.NewDeviceRejected,
                    Error = "The new device returned an invalid device type."
                };
            }

            return new DeviceEnrollmentInfoResponse
            {
                Ok = true,
                DeviceId = deviceId,
                OriginInstanceId = originInstanceId,
                DeviceType = (DeviceType)(byte)reply.DeviceType,
                TlsCertFingerprint = reply.TlsCertFingerprint,
                SignPublicKey = reply.SignPub.ToByteArray(),
                AgreementPublicKey = reply.AgreementPub.ToByteArray()
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or InvalidOperationException or CryptographicException or ArgumentException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            return new DeviceEnrollmentInfoResponse
            {
                Ok = false,
                ErrorCode = DeviceEnrollmentErrorCode.NewDeviceConnectionFailed,
                Error = ex.Message
            };
        }
    }
}
