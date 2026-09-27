using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Abstractions.State;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using PasswordManagerLocal.Common.Backend.Utils;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using static PasswordManagerLocal.Common.Backend.Constants.SyncConstants;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services.Hosted;

internal sealed class LocalDiscoveryHostedService : ISyncControlledHostedService, ILocalDiscoveryService, IDisposable
{
    private readonly IDeviceIdentityService _identity;
    private readonly ISyncDeviceIdentityService _syncDeviceIdentities;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly IDeviceSyncTaskService _deviceSyncTasks;
    private readonly IEnrollmentRuntimeState _enrollmentState;
    private readonly ILocalNetworkAddressService _networkAddresses;
    private readonly ILocalDiscoveryTransport _transport;
    private readonly ILocalDiscoveryNetworkLease _networkLease;
    private readonly IBackendExecutionProfileProvider _executionProfileProvider;
    private readonly BackendExecutionProfileChangeSignal _profileChangeSignal;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IDevicePresenceProbeService? _presenceProbeService;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _enrollmentLock = new();
    private readonly RecentLocalDiscoveryNonceCache _receivedNonces = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingSyncQueryNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSyncResponseByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAuthenticatedRequest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingEnrollmentDiscovery> _pendingEnrollmentDiscoveries = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<Guid, Device> _eligibleDiscoveryDevices = new Dictionary<Guid, Device>();
    private ActiveEnrollmentDiscoverySession? _activeEnrollmentSession;
    private CancellationTokenSource? _syncQueryLoopCancellation;
    private Task? _syncQueryLoopTask;
    private bool _started;
    private bool _networkLeaseAcquired;

    public LocalDiscoveryHostedService(
        IDeviceIdentityService identity,
        ISyncDeviceIdentityService syncDeviceIdentities,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        IDeviceSyncTaskService deviceSyncTasks,
        IEnrollmentRuntimeState enrollmentState,
        ILocalNetworkAddressService networkAddresses,
        ILocalDiscoveryTransport transport,
        ILocalDiscoveryNetworkLease networkLease,
        IBackendExecutionProfileProvider executionProfileProvider,
        IServiceScopeFactory? scopeFactory = null,
        IDevicePresenceProbeService? presenceProbeService = null)
    {
        _identity = identity;
        _syncDeviceIdentities = syncDeviceIdentities;
        _endpointRegistry = endpointRegistry;
        _deviceSyncTasks = deviceSyncTasks;
        _enrollmentState = enrollmentState;
        _networkAddresses = networkAddresses;
        _transport = transport;
        _networkLease = networkLease ?? throw new ArgumentNullException(nameof(networkLease));
        _executionProfileProvider = executionProfileProvider
            ?? throw new ArgumentNullException(nameof(executionProfileProvider));
        _profileChangeSignal = new BackendExecutionProfileChangeSignal(_executionProfileProvider);
        _scopeFactory = scopeFactory;
        _presenceProbeService = presenceProbeService;
    }


    public int StartOrder => 30;


    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_identity.IsSyncOn && !_enrollmentState.IsActive)
                return;

            if (_identity.IsSyncOn)
                await RefreshEligibleDiscoveryDevicesAsync(ct);
            else
                Volatile.Write(ref _eligibleDiscoveryDevices, new Dictionary<Guid, Device>());

            if (!_started)
            {
                await _networkLease.AcquireAsync(ct);
                _networkLeaseAcquired = true;

                try
                {
                    await _transport.StartAsync(HandleDatagramAsync, ct);
                    _started = true;
                }
                catch (Exception startException)
                {
                    try
                    {
                        await ReleaseNetworkLeaseAsync();
                    }
                    catch (Exception releaseException)
                    {
                        throw new AggregateException(startException, releaseException);
                    }

                    throw;
                }
            }

            if (_identity.IsSyncOn)
                StartSyncQueryLoopLocked();
            else
                await StopSyncQueryLoopLockedAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }


    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_started && !_networkLeaseAcquired)
                return;

            Exception? failure = null;

            try
            {
                await StopSyncQueryLoopLockedAsync();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (_started)
            {
                try
                {
                    await _transport.StopAsync(ct);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            _started = false;

            try
            {
                await ReleaseNetworkLeaseAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            _pendingSyncQueryNonces.Clear();
            _lastSyncResponseByDevice.Clear();
            _lastAuthenticatedRequest.Clear();
            _receivedNonces.Clear();
            Volatile.Write(ref _eligibleDiscoveryDevices, new Dictionary<Guid, Device>());

            if (failure is not null)
                throw failure;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }


    public void ActivateEnrollmentSession(string sessionId, byte[] secret, DateTimeOffset expiresAt)
    {
        EnsureEnrollmentAllowed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
            throw new ArgumentException("The enrollment discovery secret is missing.", nameof(secret));

        var replacement = new ActiveEnrollmentDiscoverySession
        {
            SessionId = sessionId,
            Secret = secret.ToArray(),
            ExpiresAt = expiresAt
        };

        lock (_enrollmentLock)
        {
            var previous = _activeEnrollmentSession;
            _activeEnrollmentSession = replacement;
            _lastAuthenticatedRequest.TryRemove("enrollment", out _);
            previous?.Dispose();
        }
    }


    public void DeactivateEnrollmentSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_enrollmentLock)
        {
            if (_activeEnrollmentSession is null ||
                !string.Equals(_activeEnrollmentSession.SessionId, sessionId, StringComparison.Ordinal))
                return;

            var previous = _activeEnrollmentSession;
            _activeEnrollmentSession = null;
            _lastAuthenticatedRequest.TryRemove("enrollment", out _);
            previous.Dispose();
        }
    }


    public async Task<IReadOnlyList<EnrollmentEndpoint>> FindEnrollmentEndpointsAsync(DeviceEnrollmentParsedCode parsed, CancellationToken ct = default)
    {
        EnsureEnrollmentAllowed();
        ArgumentNullException.ThrowIfNull(parsed);

        var pending = new PendingEnrollmentDiscovery
        {
            SessionId = parsed.SessionId,
            Secret = parsed.Secret.ToArray()
        };

        if (!_pendingEnrollmentDiscoveries.TryAdd(parsed.SessionId, pending))
        {
            pending.Dispose();
            throw new InvalidOperationException("An enrollment discovery is already running for this session.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(DeviceEnrollmentDiscoveryTimeoutSeconds));
        var queryLoop = SendEnrollmentQueriesAsync(pending, timeout.Token);

        try
        {
            return await pending.Completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.NewDeviceNotFound,
                "The new device could not be found on the local network. Keep the code screen open on the new device and make sure both devices are on the same local network.",
                ex);
        }
        finally
        {
            timeout.Cancel();
            _pendingEnrollmentDiscoveries.TryRemove(parsed.SessionId, out _);

            try
            {
                await queryLoop;
            }
            catch
            {
            }

            pending.Dispose();
        }
    }


    public void Dispose()
    {
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }

        lock (_enrollmentLock)
        {
            _activeEnrollmentSession?.Dispose();
            _activeEnrollmentSession = null;
        }

        foreach (var pending in _pendingEnrollmentDiscoveries.Values)
            pending.Dispose();

        _pendingEnrollmentDiscoveries.Clear();
        _profileChangeSignal.Dispose();
        _lifecycleLock.Dispose();
    }


    private async ValueTask ReleaseNetworkLeaseAsync()
    {
        if (!_networkLeaseAcquired)
            return;

        await _networkLease.ReleaseAsync();
        _networkLeaseAcquired = false;
    }


    private void StartSyncQueryLoopLocked()
    {
        if (_syncQueryLoopTask is not null)
            return;

        _syncQueryLoopCancellation = new CancellationTokenSource();
        var token = _syncQueryLoopCancellation.Token;
        _syncQueryLoopTask = Task.Run(() => SyncQueryLoopAsync(token), CancellationToken.None);
    }


    private async Task StopSyncQueryLoopLockedAsync()
    {
        var cancellation = _syncQueryLoopCancellation;
        var task = _syncQueryLoopTask;
        _syncQueryLoopCancellation = null;
        _syncQueryLoopTask = null;

        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch
        {
        }

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch
            {
            }
        }

        cancellation.Dispose();
    }


    private async Task SyncQueryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_identity.IsSyncOn)
                {
                    await RefreshEligibleDiscoveryDevicesAsync(ct);
                    await SendSyncQueryAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
            }

            try
            {
                var version = _profileChangeSignal.Version;
                var profile = _executionProfileProvider.Current;
                if (profile is null)
                    return;

                await _profileChangeSignal.WaitAsync(
                    profile.LocalDiscoveryInterval,
                    version,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }


    private async Task SendSyncQueryAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nonce = RandomNumberGenerator.GetBytes(LocalDiscoveryNonceBytes);
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            now.ToUnixTimeSeconds(),
            nonce,
            _identity.LocalDeviceId);
        var signature = _identity.Sign(authenticatedBytes);
        var packet = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, LocalDiscoverySignatureBytes);

        _pendingSyncQueryNonces[Convert.ToHexString(nonce)] = now.AddSeconds(LocalDiscoveryMaxClockSkewSeconds);
        PrunePendingSyncQueryNonces(now);
        await _transport.SendMulticastAsync(packet, ct);
    }


    private async Task SendEnrollmentQueriesAsync(PendingEnrollmentDiscovery pending, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !pending.Completion.Task.IsCompleted)
        {
            if (!_executionProfileProvider.IsEnrollmentAllowed)
                return;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var nonce = RandomNumberGenerator.GetBytes(LocalDiscoveryNonceBytes);
                var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentQueryAuthenticatedBytes(
                    now.ToUnixTimeSeconds(),
                    nonce,
                    pending.SessionId);
                var mac = LocalDiscoveryAuthenticator.ComputeMac(pending.Secret, authenticatedBytes);
                var packet = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, LocalDiscoveryMacBytes);

                pending.AddNonce(nonce, now.AddSeconds(LocalDiscoveryMaxClockSkewSeconds));
                pending.PruneNonces(now);
                await _transport.SendMulticastAsync(packet, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(LocalDiscoveryEnrollmentQueryIntervalSeconds), ct);
        }
    }


    private async Task HandleDatagramAsync(LocalDiscoveryDatagram datagram, CancellationToken ct)
    {
        if (!LocalDiscoveryPacketCodec.TryReadMessageType(datagram.Payload, out var messageType))
            return;

        switch (messageType)
        {
            case LocalDiscoveryMessageType.SyncQuery:
                await HandleSyncQueryAsync(datagram, ct);
                break;
            case LocalDiscoveryMessageType.SyncResponse:
                HandleSyncResponse(datagram);
                break;
            case LocalDiscoveryMessageType.EnrollmentQuery:
                await HandleEnrollmentQueryAsync(datagram, ct);
                break;
            case LocalDiscoveryMessageType.EnrollmentResponse:
                HandleEnrollmentResponse(datagram);
                break;
        }
    }


    private async Task HandleSyncQueryAsync(LocalDiscoveryDatagram datagram, CancellationToken ct)
    {
        if (!_identity.IsSyncOn ||
            !LocalDiscoveryPacketCodec.TryDecodeSyncQuery(datagram.Payload, out var query) ||
            !LocalDiscoveryAuthenticator.IsFresh(query.UnixTimeSeconds, DateTimeOffset.UtcNow) ||
            query.RequesterDeviceId == Guid.Empty ||
            query.RequesterDeviceId == _identity.LocalDeviceId)
            return;

        var requester = ResolveEligibleSyncDevice(query.RequesterDeviceId);
        if (requester is null)
            return;

        if (!LocalDiscoveryAuthenticator.VerifySignature(requester.SignPublicKey, query.AuthenticatedBytes, query.Signature))
            return;

        if (!_receivedNonces.TryAdd($"sync-query:{query.RequesterDeviceId:N}", query.Nonce, DateTimeOffset.UtcNow))
            return;

        if (IsAuthenticatedRequestThrottled($"sync:{query.RequesterDeviceId:N}"))
            return;

        // A discovery query is also an authenticated observation of the requester's
        // current address. Use it immediately for local pending work instead of waiting
        // for the requester to understand and accept our response. This is essential for
        // one-way pending synchronization: the requester may have nothing queued for us
        // and therefore may not currently keep our identity in its pending-peer cache.
        var requesterEndpoint = BuildObservedSyncEndpoint(
            datagram.RemoteEndpoint.Address,
            requester.TlsCertFingerprint);
        if (requesterEndpoint is not null)
        {
            _endpointRegistry.AddOrUpdate(requesterEndpoint);
            _presenceProbeService?.OnEndpointDiscovered(requester, requesterEndpoint);
            if (_syncDeviceIdentities.ContainsId(requester.Id))
                _deviceSyncTasks.TryStart(requesterEndpoint, requester);
        }

        var fingerprint = FingerprintHexToBytes(_identity.FingerprintHex);
        var responderAddress = _networkAddresses.GetRoutedLocalAddress(datagram.RemoteEndpoint.Address);
        if (fingerprint is null || responderAddress is null)
            return;

        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncResponseAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            query.Nonce,
            query.RequesterDeviceId,
            _identity.LocalDeviceId,
            fingerprint,
            responderAddress);
        var signature = _identity.Sign(authenticatedBytes);
        var response = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, LocalDiscoverySignatureBytes);

        await _transport.SendUnicastAsync(response, datagram.RemoteEndpoint, ct);
    }


    private void HandleSyncResponse(LocalDiscoveryDatagram datagram)
    {
        if (!_identity.IsSyncOn ||
            !LocalDiscoveryPacketCodec.TryDecodeSyncResponse(datagram.Payload, out var response) ||
            !LocalDiscoveryAuthenticator.IsFresh(response.UnixTimeSeconds, DateTimeOffset.UtcNow) ||
            response.RequesterDeviceId != _identity.LocalDeviceId ||
            response.ResponderDeviceId == Guid.Empty ||
            response.ResponderDeviceId == _identity.LocalDeviceId ||
            !response.ResponderAddress.Equals(datagram.RemoteEndpoint.Address) ||
            !IsPendingSyncQueryNonce(response.QueryNonce, DateTimeOffset.UtcNow))
            return;

        var device = ResolveEligibleSyncDevice(response.ResponderDeviceId);
        if (device is null)
            return;

        var expectedFingerprint = FingerprintHexToBytes(device.TlsCertFingerprint);
        if (expectedFingerprint is null || !CryptographicOperations.FixedTimeEquals(expectedFingerprint, response.TlsFingerprint))
            return;

        if (!LocalDiscoveryAuthenticator.VerifySignature(device.SignPublicKey, response.AuthenticatedBytes, response.Signature))
            return;

        if (!_receivedNonces.TryAdd($"sync-response:{response.ResponderDeviceId:N}", response.QueryNonce, DateTimeOffset.UtcNow))
            return;

        var endpoint = BuildObservedSyncEndpoint(response.ResponderAddress, device.TlsCertFingerprint);
        if (endpoint is null)
            return;

        _endpointRegistry.AddOrUpdate(endpoint);
        _presenceProbeService?.OnEndpointDiscovered(device, endpoint);

        if (IsSyncResponseThrottled(response.ResponderDeviceId, DateTimeOffset.UtcNow))
            return;

        if (_syncDeviceIdentities.ContainsId(device.Id))
            _deviceSyncTasks.TryStart(endpoint, device);
    }


    private Device? ResolveEligibleSyncDevice(Guid deviceId)
    {
        if (_syncDeviceIdentities.TryGetById(deviceId, out var cachedDevice) && cachedDevice is not null)
            return cachedDevice;

        var eligibleDevices = Volatile.Read(ref _eligibleDiscoveryDevices);
        return eligibleDevices.TryGetValue(deviceId, out var eligibleDevice)
            ? eligibleDevice
            : null;
    }


    private async Task RefreshEligibleDiscoveryDevicesAsync(CancellationToken ct)
    {
        if (_scopeFactory is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        var localUsers = scope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>();
        var enabledUserIds = await localUsers.ListSyncOnUserIdsAsync(ct);
        if (enabledUserIds.Count == 0)
        {
            Volatile.Write(ref _eligibleDiscoveryDevices, new Dictionary<Guid, Device>());
            return;
        }

        var userDevices = scope.ServiceProvider.GetRequiredService<IUserDeviceRepository>();
        var links = await userDevices.ListByUsersWithDevicesAsync(enabledUserIds, ct);
        var refreshed = new Dictionary<Guid, Device>();

        foreach (var link in links)
        {
            if (link.IsDeleted || !link.IsSyncOn || !CanUseForAuthenticatedDiscovery(link.Device))
                continue;

            try
            {
                link.VerifyIntegrity();
                link.Device!.VerifyIntegrity();
            }
            catch
            {
                continue;
            }

            refreshed[link.DeviceId] = CloneDiscoveryDevice(link.Device!);
        }

        Volatile.Write(ref _eligibleDiscoveryDevices, refreshed);
    }


    private bool CanUseForAuthenticatedDiscovery(Device? device) =>
        device is not null &&
        device.Id != Guid.Empty &&
        device.IsTrusted &&
        !device.IsBlocked &&
        device.PublicKey.Length != 0 &&
        device.SignPublicKey.Length != 0 &&
        !string.IsNullOrWhiteSpace(device.TlsCertFingerprint);


    private Device CloneDiscoveryDevice(Device device) =>
        new()
        {
            Id = device.Id,
            PublicKey = device.PublicKey.ToArray(),
            SignPublicKey = device.SignPublicKey.ToArray(),
            SignPublicKeyHash = device.SignPublicKeyHash.ToArray(),
            TlsCertFingerprint = device.TlsCertFingerprint,
            IsTrusted = device.IsTrusted,
            IsBlocked = device.IsBlocked,
            IntegrityHash = device.IntegrityHash.ToArray()
        };


    private async Task HandleEnrollmentQueryAsync(LocalDiscoveryDatagram datagram, CancellationToken ct)
    {
        if (!_executionProfileProvider.IsEnrollmentAllowed ||
            !LocalDiscoveryPacketCodec.TryDecodeEnrollmentQuery(datagram.Payload, out var query) ||
            !LocalDiscoveryAuthenticator.IsFresh(query.UnixTimeSeconds, DateTimeOffset.UtcNow))
            return;

        byte[]? secret = null;
        try
        {
            lock (_enrollmentLock)
            {
                if (_activeEnrollmentSession is null ||
                    _activeEnrollmentSession.ExpiresAt <= DateTimeOffset.UtcNow ||
                    !string.Equals(_activeEnrollmentSession.SessionId, query.SessionId, StringComparison.Ordinal))
                    return;

                secret = _activeEnrollmentSession.Secret.ToArray();
            }

            if (!LocalDiscoveryAuthenticator.VerifyMac(secret, query.AuthenticatedBytes, query.Mac))
                return;

            if (!_receivedNonces.TryAdd($"enrollment-query:{query.SessionId}", query.Nonce, DateTimeOffset.UtcNow))
                return;

            if (IsAuthenticatedRequestThrottled("enrollment"))
                return;

            var fingerprint = FingerprintHexToBytes(_identity.FingerprintHex);
            var responderAddress = _networkAddresses.GetRoutedLocalAddress(datagram.RemoteEndpoint.Address);
            if (fingerprint is null || responderAddress is null)
                return;

            var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentResponseAuthenticatedBytes(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                query.Nonce,
                query.SessionId,
                _identity.LocalDeviceId,
                _identity.OriginInstanceId,
                _identity.DeviceType,
                fingerprint,
                _identity.SignPublicKey,
                _identity.AgreementPublicKey,
                responderAddress);
            var mac = LocalDiscoveryAuthenticator.ComputeMac(secret, authenticatedBytes);
            var response = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, LocalDiscoveryMacBytes);

            await _transport.SendUnicastAsync(response, datagram.RemoteEndpoint, ct);
        }
        finally
        {
            if (secret is not null)
                CryptographicOperations.ZeroMemory(secret);
        }
    }


    private void HandleEnrollmentResponse(LocalDiscoveryDatagram datagram)
    {
        if (!_executionProfileProvider.IsEnrollmentAllowed ||
            !LocalDiscoveryPacketCodec.TryDecodeEnrollmentResponse(datagram.Payload, out var response) ||
            !LocalDiscoveryAuthenticator.IsFresh(response.UnixTimeSeconds, DateTimeOffset.UtcNow) ||
            !_pendingEnrollmentDiscoveries.TryGetValue(response.SessionId, out var pending) ||
            !pending.ContainsNonce(response.QueryNonce, DateTimeOffset.UtcNow) ||
            response.DeviceId == Guid.Empty || response.OriginInstanceId == Guid.Empty || !DeviceTypeDetector.IsValid(response.DeviceType) ||
            response.DeviceId == _identity.LocalDeviceId ||
            !response.ResponderAddress.Equals(datagram.RemoteEndpoint.Address) ||
            _identity.SignPublicKey.SequenceEqual(response.SignPublicKey) ||
            !LocalDiscoveryAuthenticator.VerifyMac(pending.Secret, response.AuthenticatedBytes, response.Mac))
            return;

        var endpoint = BuildObservedEnrollmentEndpoint(response.ResponderAddress, response);
        if (endpoint is null)
            return;

        pending.Completion.TrySetResult([endpoint]);
    }


    private EnrollmentEndpoint? BuildObservedEnrollmentEndpoint(
        IPAddress observedSourceAddress,
        EnrollmentDiscoveryResponsePacket response)
    {
        var host = observedSourceAddress.ToString();
        if (_networkAddresses.GetRemoteEndpointPriority(host) == int.MinValue)
            return null;

        BackendDebugLog.Info($"Authenticated local discovery enrollment endpoint resolved from the UDP response source: {host}:{SyncPort}.");
        return new EnrollmentEndpoint
        {
            Host = host,
            Port = SyncPort,
            DeviceId = response.DeviceId,
            OriginInstanceId = response.OriginInstanceId,
            DeviceType = response.DeviceType,
            TlsCertFingerprint = Convert.ToHexString(response.TlsFingerprint),
            SignPublicKey = response.SignPublicKey.ToArray(),
            AgreementPublicKey = response.AgreementPublicKey.ToArray()
        };
    }


    private DiscoveredDeviceEndpoint? BuildObservedSyncEndpoint(
        IPAddress observedSourceAddress,
        string tlsFingerprint)
    {
        var host = observedSourceAddress.ToString();
        if (_networkAddresses.GetRemoteEndpointPriority(host) == int.MinValue)
            return null;

        return new DiscoveredDeviceEndpoint
        {
            Host = host,
            Port = SyncPort,
            TlsCertFingerprint = tlsFingerprint
        };
    }


    private bool IsAuthenticatedRequestThrottled(string key)
    {
        var now = DateTimeOffset.UtcNow;

        if (_lastAuthenticatedRequest.TryGetValue(key, out var lastRequest) &&
            now - lastRequest < TimeSpan.FromMilliseconds(LocalDiscoveryRequestThrottleMilliseconds))
            return true;

        _lastAuthenticatedRequest[key] = now;
        return false;
    }


    private bool IsSyncResponseThrottled(Guid deviceId, DateTimeOffset now)
    {
        var key = deviceId.ToString("N");
        if (_lastSyncResponseByDevice.TryGetValue(key, out var lastResponse) &&
            now - lastResponse < TimeSpan.FromSeconds(LocalDiscoveryPeerThrottleSeconds))
            return true;

        _lastSyncResponseByDevice[key] = now;
        return false;
    }


    private bool IsPendingSyncQueryNonce(byte[] nonce, DateTimeOffset now)
    {
        var key = Convert.ToHexString(nonce);
        return _pendingSyncQueryNonces.TryGetValue(key, out var expiresAt) && expiresAt >= now;
    }


    private void PrunePendingSyncQueryNonces(DateTimeOffset now)
    {
        foreach (var item in _pendingSyncQueryNonces)
        {
            if (item.Value < now)
                _pendingSyncQueryNonces.TryRemove(item.Key, out _);
        }
    }


    private void EnsureEnrollmentAllowed()
    {
        if (_executionProfileProvider.IsEnrollmentAllowed)
            return;

        throw new DeviceEnrollmentException(
            DeviceEnrollmentErrorCode.InteractiveSessionRequired,
            "Device enrollment requires an active interactive session.");
    }


    private byte[]? FingerprintHexToBytes(string fingerprint)
    {
        try
        {
            var normalized = FingerprintUtil.NormalizeOrEmpty(fingerprint);
            if (normalized.Length != 64)
                return null;

            var bytes = Convert.FromHexString(normalized);
            return bytes.Length == 32 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
}
