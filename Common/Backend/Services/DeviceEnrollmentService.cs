using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Abstractions.Sync.Presence;
using PasswordManagerLocal.Common.Backend.Abstractions.Persistence;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Contracts.Responses;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Enrollment;
using System.Security.Cryptography;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Utils;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Presence;

using PasswordManagerLocal.Common.Backend.Diagnostics;

namespace PasswordManagerLocal.Common.Backend.Services;

/// <summary>
/// Owns enrollment session state and orchestrates the high-level enrollment protocol. Endpoint
/// validation, device registration, snapshot handling, transfer, and import are delegated to focused services.
/// </summary>
public sealed class DeviceEnrollmentService : IDeviceEnrollmentService, IDeviceEnrollmentLifecycleCoordinator, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceIdentityService _identity;
    private readonly IDiscoveredDeviceEndpointRegistry _endpointRegistry;
    private readonly ISyncRuntimeService _syncRuntime;
    private readonly ILocalDiscoveryService _localDiscovery;
    private readonly IDeviceEnrollmentEndpointService _endpointService;
    private readonly IDeviceEnrollmentRegistrationService _registrationService;
    private readonly IDeviceEnrollmentSnapshotService _snapshotService;
    private readonly IDeviceEnrollmentSnapshotTransferService _snapshotTransferService;
    private readonly IDeviceEnrollmentSnapshotImporterService _snapshotImporter;
    private readonly IInteractiveUserDataStateAccessor _interactiveState;
    private readonly IDeviceEnrollmentAvailability _enrollmentAvailability;
    private readonly IDevicePresenceRegistry? _presenceRegistry;
    private readonly object _lock = new();
    private EnrollmentSession? _currentSession;
    private CancellationTokenSource? _enrollmentExpirationCancellation;
    private TaskCompletionSource? _enrollmentOperationsDrained;
    private CancellationTokenSource? _interactiveAdmissionCancellation;
    private int _activeEnrollmentOperations;
    private bool _interactiveAdmissionOpen;

    public DeviceEnrollmentService(
        IServiceScopeFactory scopeFactory,
        IDeviceIdentityService identity,
        IDiscoveredDeviceEndpointRegistry endpointRegistry,
        ISyncRuntimeService syncRuntime,
        ILocalDiscoveryService localDiscovery,
        IDeviceEnrollmentEndpointService endpointService,
        IDeviceEnrollmentRegistrationService registrationService,
        IDeviceEnrollmentSnapshotService snapshotService,
        IDeviceEnrollmentSnapshotTransferService snapshotTransferService,
        IDeviceEnrollmentSnapshotImporterService snapshotImporter,
        IInteractiveUserDataStateAccessor interactiveState,
        IDeviceEnrollmentAvailability enrollmentAvailability,
        IDevicePresenceRegistry? presenceRegistry = null)
    {
        _scopeFactory = scopeFactory;
        _identity = identity;
        _endpointRegistry = endpointRegistry;
        _syncRuntime = syncRuntime;
        _localDiscovery = localDiscovery;
        _endpointService = endpointService;
        _registrationService = registrationService;
        _snapshotService = snapshotService;
        _snapshotTransferService = snapshotTransferService;
        _snapshotImporter = snapshotImporter;
        _interactiveState = interactiveState;
        _enrollmentAvailability = enrollmentAvailability
            ?? throw new ArgumentNullException(nameof(enrollmentAvailability));
        _presenceRegistry = presenceRegistry;
    }

    public void OpenInteractiveAdmission()
    {
        lock (_lock)
        {
            if (!_enrollmentAvailability.IsEnrollmentAllowed)
            {
                throw new InvalidOperationException(
                    "Enrollment admission cannot open without an active interactive runtime reason.");
            }

            if (_activeEnrollmentOperations != 0)
                throw new InvalidOperationException("Enrollment operations are still draining.");

            _interactiveAdmissionCancellation?.Dispose();
            _interactiveAdmissionCancellation = new CancellationTokenSource();
            _interactiveAdmissionOpen = true;
        }
    }


    public async Task CloseInteractiveAdmissionAsync(CancellationToken cancellationToken = default)
    {
        Task? operationsDrained = null;
        CancellationTokenSource? admissionCancellation;
        lock (_lock)
        {
            _interactiveAdmissionOpen = false;
            admissionCancellation = _interactiveAdmissionCancellation;
            if (_activeEnrollmentOperations != 0)
            {
                _enrollmentOperationsDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                operationsDrained = _enrollmentOperationsDrained.Task;
            }
        }

        admissionCancellation?.Cancel();

        if (operationsDrained is not null)
            await operationsDrained.WaitAsync(cancellationToken);

        lock (_lock)
        {
            if (ReferenceEquals(_interactiveAdmissionCancellation, admissionCancellation))
                _interactiveAdmissionCancellation = null;
        }
        admissionCancellation?.Dispose();

        await CancelEnrollmentCoreAsync(cancellationToken);
    }


    public async Task<DeviceEnrollmentCodeResponse> StartEnrollmentAsync(CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            enrollmentOperation.AdmissionCancellationToken);
        var operationToken = operationCancellation.Token;
        await _syncRuntime.BeginEnrollmentOnlyAsync(operationToken);
        EnrollmentSession? session = null;

        try
        {
            DeviceEnrollmentDirectEndpointInfo directEndpointInfo;

            lock (_lock)
            {
                CancelEnrollmentExpirationLocked();
                DeactivateEnrollmentDiscoveryLocked();
                _currentSession?.ClearSensitiveData();
                directEndpointInfo = _endpointService.BuildDirectEndpointInfo();
                var generated = DeviceEnrollmentCode.Create(directEndpointInfo);
                session = new EnrollmentSession
                {
                    SessionId = generated.SessionId,
                    Secret = generated.Secret,
                    Code = generated.Code,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    State = DeviceEnrollmentState.Waiting
                };
                _currentSession = session;
                _localDiscovery.ActivateEnrollmentSession(session.SessionId, session.Secret, session.ExpiresAt);
                StartEnrollmentExpirationCountdownLocked(session);
            }

            await _endpointService.VerifyLocalEnrollmentListenerAsync(session.SessionId, session.Secret, directEndpointInfo, operationToken);
            return new DeviceEnrollmentCodeResponse { Code = session.Code, ExpiresAt = session.ExpiresAt };
        }
        catch
        {
            lock (_lock)
            {
                if (session is not null && ReferenceEquals(_currentSession, session))
                {
                    CancelEnrollmentExpirationLocked();
                    DeactivateEnrollmentDiscoveryLocked();
                    session.ClearSensitiveData();
                    _currentSession = null;
                }
            }

            await _syncRuntime.EndEnrollmentOnlyAsync(CancellationToken.None);
            throw;
        }
    }


    public async Task<DeviceEnrollmentStatusResponse> GetEnrollmentStatusAsync(CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        DeviceEnrollmentStatusResponse response;
        var endTemporaryMode = false;
        lock (_lock)
        {
            if (_currentSession is null)
                return new DeviceEnrollmentStatusResponse { State = DeviceEnrollmentState.None };
            ExpireSessionIfNeededLocked();
            response = new DeviceEnrollmentStatusResponse
            {
                State = _currentSession.State,
                ErrorMessage = _currentSession.ErrorMessage,
                ErrorCode = _currentSession.ErrorCode,
                ExpiresAt = _currentSession.ExpiresAt
            };
            endTemporaryMode = _currentSession.State is DeviceEnrollmentState.Expired or DeviceEnrollmentState.Failed or DeviceEnrollmentState.Completed;
        }
        if (endTemporaryMode)
            await _syncRuntime.EndEnrollmentOnlyAsync(ct);
        return response;
    }


    public async Task CancelEnrollmentAsync(CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        await CancelEnrollmentCoreAsync(ct);
    }


    public async Task AddDeviceByCodeAsync(Guid token, string code, CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            enrollmentOperation.AdmissionCancellationToken);
        var operationToken = operationCancellation.Token;
        using (var authorizationScope = _scopeFactory.CreateScope())
        {
            var users = authorizationScope.ServiceProvider.GetRequiredService<IUserLookupService>();
            var localUsers = authorizationScope.ServiceProvider.GetRequiredService<ILocalUserDeviceRepository>();
            var user = await users.GetAndVerifyUserAsync(token, operationToken);
            if (!await localUsers.IsSyncOnAsync(user.UId, operationToken))
                throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.SyncDisabled, "Synchronization is disabled for this profile on the current device.");
        }

        DeviceEnrollmentParsedCode parsed;
        try
        {
            parsed = DeviceEnrollmentCode.Parse(code);
        }
        catch (InvalidDataException ex)
        {
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.InvalidCode, ex.Message, ex);
        }

        RejectLocalDeviceEnrollmentCode(parsed);

        var directEndpointCandidates = parsed.DirectEndpoints
            .Select(_endpointService.ToEnrollmentEndpoint)
            .Where(endpoint => !_endpointService.IsLocalEndpoint(endpoint))
            .GroupBy(endpoint => $"{endpoint.Host}:{endpoint.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(endpoint => new { Endpoint = endpoint, Priority = _endpointService.GetDirectEndpointPriorityForThisDevice(endpoint) })
            .ToList();

        var directEndpoints = directEndpointCandidates
            .Where(candidate => candidate.Priority > int.MinValue)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Endpoint.Host, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Endpoint)
            .ToList();

        var hasAuthoritativeSameSubnetDirectEndpoint = directEndpointCandidates.Any(candidate => candidate.Priority >= 2000);
        var directFailures = new List<string>();
        var discoveryFailures = new List<string>();
        var attemptedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        BackendDebugLog.Info($"AddDeviceByCode started. Session={parsed.SessionId}, directEndpointCount={directEndpoints.Count}, authoritativeDirect={hasAuthoritativeSameSubnetDirectEndpoint}, directEndpointCandidates={string.Join(", ", directEndpointCandidates.Select(e => $"{e.Endpoint.Host}:{e.Endpoint.Port}/priority={e.Priority}"))}");

        foreach (var endpoint in directEndpoints)
        {
            try
            {
                attemptedEndpoints.Add($"{endpoint.Host}:{endpoint.Port}");
                BackendDebugLog.Info($"Trying direct enrollment endpoint {endpoint.Host}:{endpoint.Port}.");
                await CompleteEnrollmentWithEndpointAsync(token, parsed, endpoint, enrollmentOperation, operationToken, ct);
                BackendDebugLog.Info($"Direct enrollment endpoint {endpoint.Host}:{endpoint.Port} completed successfully.");
                return;
            }
            catch (DeviceEnrollmentException ex) when (ex.ErrorCode == DeviceEnrollmentErrorCode.NewDeviceConnectionFailed)
            {
                directFailures.Add($"{endpoint.Host}:{endpoint.Port} -> {ex.Message}");
                BackendDebugLog.Error($"Direct enrollment endpoint {endpoint.Host}:{endpoint.Port} failed with a connection/transfer error: {ex.Message}", ex);
            }
        }

        if (directEndpoints.Count > 0)
        {
            var reason = hasAuthoritativeSameSubnetDirectEndpoint
                ? "The same-subnet direct endpoint did not respond. Trying authenticated local discovery fallback because Wi-Fi/wired bridges can still expose another usable address."
                : "The direct enrollment endpoints did not respond. Trying authenticated local discovery fallback.";

            BackendDebugLog.Info(reason);
        }

        IReadOnlyList<EnrollmentEndpoint> discoveryEndpoints;
        try
        {
            BackendDebugLog.Info("Trying authenticated local enrollment discovery fallback.");
            discoveryEndpoints = await _localDiscovery.FindEnrollmentEndpointsAsync(parsed, operationToken);
        }
        catch (DeviceEnrollmentException ex) when (directFailures.Count > 0)
        {
            throw new DeviceEnrollmentException(
                DeviceEnrollmentErrorCode.NewDeviceConnectionFailed,
                BuildEndpointFailureMessage(
                    BuildLocalDiscoveryFailedMessage(hasAuthoritativeSameSubnetDirectEndpoint),
                    directFailures),
                ex);
        }

        foreach (var endpoint in discoveryEndpoints)
        {
            var endpointKey = $"{endpoint.Host}:{endpoint.Port}";
            var isAuthenticatedRediscoveryRetry = !attemptedEndpoints.Add(endpointKey);

            if (isAuthenticatedRediscoveryRetry)
            {
                BackendDebugLog.Info($"Retrying enrollment endpoint {endpointKey} because a fresh authenticated local discovery response confirmed that the same enrollment session is still active there.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), operationToken);
            }

            try
            {
                var attemptKind = isAuthenticatedRediscoveryRetry ? "authenticated-discovery-confirmed retry" : "authenticated local discovery endpoint";
                BackendDebugLog.Info($"Trying {attemptKind} {endpoint.Host}:{endpoint.Port}.");
                await CompleteEnrollmentWithEndpointAsync(token, parsed, endpoint, enrollmentOperation, operationToken, ct);
                BackendDebugLog.Info($"{attemptKind} {endpoint.Host}:{endpoint.Port} completed successfully.");
                return;
            }
            catch (DeviceEnrollmentException ex) when (ex.ErrorCode == DeviceEnrollmentErrorCode.NewDeviceConnectionFailed)
            {
                var attemptKind = isAuthenticatedRediscoveryRetry ? "authenticated-discovery-confirmed retry" : "authenticated local discovery";
                discoveryFailures.Add($"{endpoint.Host}:{endpoint.Port} ({attemptKind}) -> {ex.Message}");
                BackendDebugLog.Error($"{attemptKind} enrollment endpoint {endpoint.Host}:{endpoint.Port} failed with a connection/transfer error: {ex.Message}", ex);
            }
        }

        throw new DeviceEnrollmentException(
            DeviceEnrollmentErrorCode.NewDeviceConnectionFailed,
            BuildEndpointFailureMessage(
                BuildNoEndpointAcceptedMessage(hasAuthoritativeSameSubnetDirectEndpoint, discoveryEndpoints.Count > 0),
                directFailures.Concat(discoveryFailures)));
    }


    private string BuildEndpointFailureMessage(string message, IEnumerable<string> endpointFailures)
    {
        var failures = endpointFailures
            .Where(failure => !string.IsNullOrWhiteSpace(failure))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (failures.Count == 0)
            return message;

        return $"{message} Attempted endpoints: {string.Join("; ", failures)}";
    }


    private string BuildNoEndpointAcceptedMessage(bool hadSameSubnetDirectEndpoint, bool authenticatedDiscoveryResponseReceived)
    {
        if (hadSameSubnetDirectEndpoint && authenticatedDiscoveryResponseReceived)
        {
            return "The enrollment code contained a same-subnet address and the old device received a fresh authenticated local discovery response from that enrollment session, but TCP port 26688 still did not complete a connection. The app retried the local discovery-confirmed address. The failure happened before device identity verification or profile transfer, so the remaining problem is in the TCP-specific path between the two Windows devices, such as effective OS filtering, adapter routing, a transient Wi-Fi/ARP state, or a duplicate IP address.";
        }

        if (hadSameSubnetDirectEndpoint)
        {
            return "The enrollment code contained a same-subnet address for the new device, but no known or authenticated-discovery address completed the authenticated TCP enrollment connection. The failure happened before device identity verification or profile transfer. Keep the enrollment screen open and verify that the target app is still listening on TCP port 26688 and that TCP traffic reaches that process.";
        }

        return "The new device was discovered, but none of the reachable network addresses accepted the enrollment transfer.";
    }


    private string BuildLocalDiscoveryFailedMessage(bool hadSameSubnetDirectEndpoint)
    {
        if (hadSameSubnetDirectEndpoint)
        {
            return "The enrollment code contained a same-subnet address for the new device, but the authenticated TCP enrollment connection could not be completed and authenticated local discovery also failed. Keep the enrollment screen open and verify that the target app is still listening on TCP port 26688 and that inbound traffic reaches that process.";
        }

        return "The new device was included in the enrollment code, but it could not be reached directly and authenticated local discovery also failed.";
    }


    private void RejectLocalDeviceEnrollmentCode(DeviceEnrollmentParsedCode parsed)
    {
        lock (_lock)
        {
            if (_currentSession is not null &&
                string.Equals(_currentSession.SessionId, parsed.SessionId, StringComparison.Ordinal) &&
                DeviceEnrollmentCode.FixedTimeEquals(_currentSession.Secret, parsed.Secret))
                throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.InvalidCode, "The enrollment code belongs to this local device.");
        }

        if (parsed.DirectEndpoints.Any(endpoint =>
                _endpointService.IsLocalDeviceIdentity(endpoint.DeviceId, endpoint.SignPublicKey, endpoint.TlsCertFingerprint)))
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.InvalidCode, "The enrollment code belongs to this local device.");
    }


    private async Task CompleteEnrollmentWithEndpointAsync(
        Guid token,
        DeviceEnrollmentParsedCode parsed,
        EnrollmentEndpoint endpoint,
        DeviceEnrollmentOperationLease enrollmentOperation,
        CancellationToken preCriticalToken,
        CancellationToken ct)
    {
        BackendDebugLog.Info($"Enrollment connection and identity check started for {endpoint.Host}:{endpoint.Port}. HasEmbeddedIdentity={endpoint.DeviceId != Guid.Empty}.");
        endpoint = await _endpointService.ResolveEndpointIdentityAsync(endpoint, parsed, preCriticalToken);
        BackendDebugLog.Info($"Enrollment identity resolved for {endpoint.Host}:{endpoint.Port}. DeviceId={endpoint.DeviceId}, Origin={endpoint.OriginInstanceId}, TlsFingerprintPrefix={FingerprintUtil.Normalize(endpoint.TlsCertFingerprint)[..Math.Min(16, FingerprintUtil.Normalize(endpoint.TlsCertFingerprint).Length)]}.");

        Guid userId;
        PasswordManagerLocal.Common.Backend.Security.EncryptionKey mergeKey;
        using (var authorizationScope = _scopeFactory.CreateScope())
        {
            var users = authorizationScope.ServiceProvider.GetRequiredService<IUserLookupService>();
            userId = (await users.GetAndVerifyUserAsync(token, preCriticalToken)).UId;
            mergeKey = _interactiveState.GetEncryptionKeyFromToken(token);
        }

        enrollmentOperation.EnterCriticalSection();

        Guid commitId = Guid.Empty;
        Guid additionOperationId = Guid.Empty;
        var authoritativeCommitAttempted = false;
        var authoritativeCommitCompleted = false;
        var transferCompleted = false;
        try
        {
            using (mergeKey)
            using (var scope = _scopeFactory.CreateScope())
            {
                var lifecycle = scope.ServiceProvider.GetRequiredService<IUserLifecycleCoordinator>();
                await lifecycle.ExecuteAsync(userId, async lifecycleToken =>
                {
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var users = scope.ServiceProvider.GetRequiredService<IUserLookupService>();
                    var snapshotMerge = scope.ServiceProvider.GetRequiredService<IUserSnapshotMergeCoordinator>();
                    var snapshots = scope.ServiceProvider.GetRequiredService<IUserSyncSnapshotRepository>();
                    var controlStates = scope.ServiceProvider.GetRequiredService<IUserControlStateRepository>();
                    var controlOperations = scope.ServiceProvider.GetRequiredService<IUserControlOperationRepository>();
                    var commits = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentCommitRepository>();
                    var writer = scope.ServiceProvider.GetRequiredService<IUserControlOperationWriterService>();
                    var userDataReader = scope.ServiceProvider.GetRequiredService<IUserDataReaderService>();
                    var userDataWriter = scope.ServiceProvider.GetRequiredService<IUserDataWriterService>();
                    var publisher = scope.ServiceProvider.GetRequiredService<IUserSnapshotPublisherService>();

                    await _registrationService.RegisterRemoteDeviceAsync(scope.ServiceProvider, userId, endpoint, lifecycleToken);
                    var canonicalUser = await users.GetAndVerifyUserAsync(token, lifecycleToken);
                    var controlState = await controlStates.GetAsync(userId, lifecycleToken);
                    if (controlState?.HasConflict == true)
                        throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceRejected, controlState.ConflictReason ?? "The account control plane is quarantined.");
                    if ((await snapshots.ListForUserAsync(userId, lifecycleToken)).Any(row => row.Status == UserSyncSnapshotStatus.Quarantined))
                        throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.NewDeviceRejected, "Enrollment is blocked by unresolved snapshot fork evidence.");

                    await snapshotMerge.TryMergePendingUnderLifecycleAsync(userId, mergeKey, UserSyncKeyConfidence.AuthenticatedSession, lifecycleToken);
                    canonicalUser = await users.GetAndVerifyUserAsync(token, lifecycleToken);

                    // Reserve the encrypted-device item version before opening the authoritative SQLite
                    // transaction. The durable clock persists through a separate DbContext, so advancing it
                    // while this transaction owns the SQLite write lock would deadlock enrollment. Version
                    // stamps may have gaps, therefore reserving one for a recoverable enrollment is safe.
                    var encryptedDeviceVersion = scope.ServiceProvider
                        .GetRequiredService<ISyncVersionClockService>()
                        .Next();

                    await using var transaction = await unitOfWork.BeginTransactionAsync(lifecycleToken);
                    try
                    {
                        var commit = await commits.GetRecoverableAsync(userId, endpoint.DeviceId, endpoint.OriginInstanceId, lifecycleToken);
                        if (commit is not null)
                        {
                            try
                            {
                                ValidateRecoverableEnrollmentCommit(commit, endpoint);
                            }
                            catch (DeviceEnrollmentException ex)
                            {
                                throw CreatePartialCommitException(commit, ex.ErrorCode, ex.Message, commit.Status != DeviceEnrollmentCommitStatus.Transferred, ex);
                            }

                            var existingOperation = await controlOperations.GetByIdAsync(commit.AdditionOperationId, lifecycleToken);
                            if (existingOperation is null || existingOperation.OperationType != UserControlOperationType.DeviceAddition || existingOperation.Status != UserControlOperationStatus.Applied ||
                                !Hashing.Verify(existingOperation.OperationHash, commit.AdditionOperationHash))
                            {
                                throw CreatePartialCommitException(
                                    commit,
                                    DeviceEnrollmentErrorCode.NewDeviceRejected,
                                    "The recoverable enrollment addition operation is missing or inconsistent.",
                                    commit.Status != DeviceEnrollmentCommitStatus.Transferred);
                            }

                            commit.Status = DeviceEnrollmentCommitStatus.PendingTransfer;
                            commit.LastAttemptAtUtc = DateTimeOffset.UtcNow;
                            commit.LastError = null;
                            commit.Version = checked(commit.Version + 1);
                            commits.Update(commit);
                        }
                        else
                        {
                            var envelope = await writer.CreateAppliedDeviceAdditionUnderLifecycleAsync(canonicalUser, endpoint, lifecycleToken);
                            canonicalUser = await users.GetAndVerifyUserAsync(token, lifecycleToken);

                            // Encrypted device metadata is derived only after the signed addition has been applied.
                            await _snapshotService.EnsureEncryptedDeviceDataAsync(
                                userDataReader,
                                userDataWriter,
                                canonicalUser,
                                token,
                                endpoint.DeviceId,
                                encryptedDeviceVersion,
                                lifecycleToken);
                            canonicalUser = await users.GetAndVerifyUserAsync(token, lifecycleToken);
                            await publisher.GetOrCreateAsync(canonicalUser, lifecycleToken);
                            await _registrationService.QueueInitialSyncAsync(scope.ServiceProvider, userId, endpoint.DeviceId, lifecycleToken);

                            commit = new DeviceEnrollmentCommit
                            {
                                UserId = userId,
                                TargetDeviceId = endpoint.DeviceId,
                                TargetOriginInstanceId = endpoint.OriginInstanceId,
                                TargetSignPublicKeyHash = Hashing.SHA256Hash(endpoint.SignPublicKey),
                                TargetAgreementPublicKeyHash = Hashing.SHA256Hash(endpoint.AgreementPublicKey),
                                TargetTlsCertFingerprint = SyncIdentityUtil.NormalizeFingerprint(endpoint.TlsCertFingerprint),
                                TargetDeviceType = endpoint.DeviceType,
                                AdditionOperationId = envelope.OperationId,
                                AdditionOperationHash = envelope.OperationHash.ToArray(),
                                Status = DeviceEnrollmentCommitStatus.PendingTransfer,
                                CreatedAtUtc = DateTimeOffset.UtcNow,
                                LastAttemptAtUtc = DateTimeOffset.UtcNow
                            };
                            await commits.AddAsync(commit, lifecycleToken);
                        }

                        await unitOfWork.SaveChangesAsync(lifecycleToken);
                        authoritativeCommitAttempted = true;
                        await transaction.CommitAsync(lifecycleToken);
                        authoritativeCommitCompleted = true;
                        commitId = commit.CommitId;
                        additionOperationId = commit.AdditionOperationId;
                    }
                    catch
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        unitOfWork.ClearTrackedChanges();
                        throw;
                    }
                }, ct);
            }

            using (var activationScope = _scopeFactory.CreateScope())
            {
                try
                {
                    await activationScope.ServiceProvider.GetRequiredService<IPendingSyncActivationService>()
                        .ActivatePendingAsync(CancellationToken.None);
                }
                catch
                {
                    // Durable work remains discoverable after the authoritative addition commit.
                }
            }

            DeviceEnrollmentSnapshot snapshot;
            using (var bootstrapScope = _scopeFactory.CreateScope())
                snapshot = await _snapshotService.BuildAsync(bootstrapScope.ServiceProvider, userId, endpoint, additionOperationId, ct);

            var proof = DeviceEnrollmentCode.BuildCompletionProof(
                parsed.SessionId,
                parsed.Secret,
                _identity.LocalDeviceId.ToString("N"),
                _identity.OriginInstanceId,
                _identity.SignPublicKey,
                _identity.FingerprintHex,
                endpoint.DeviceId,
                endpoint.OriginInstanceId);

            // The authoritative addition is committed before this network transfer. Failure is recoverable
            // and must never roll the membership epoch backward or create a second same-base transition.
            var result = await _snapshotTransferService.SendAsync(endpoint, parsed.SessionId, parsed.Secret, proof, snapshot, ct);
            transferCompleted = result.Ok;
            await UpdateEnrollmentCommitAfterTransferAsync(commitId, result.Ok, result.Error, ct);
            if (!result.Ok)
            {
                throw CreatePartialCommitException(
                    commitId,
                    endpoint,
                    result.ErrorCode,
                    result.Error ?? "The new device rejected the enrollment request.",
                    transferPending: true);
            }

            using (var completionScope = _scopeFactory.CreateScope())
            {
                _interactiveState.InvalidateToken(token);
                var devices = completionScope.ServiceProvider.GetRequiredService<IDeviceRepository>();
                var syncIdentities = completionScope.ServiceProvider.GetRequiredService<ISyncDeviceIdentityService>();
                var pendingSyncActivation = completionScope.ServiceProvider.GetRequiredService<IPendingSyncActivationService>();
                var remoteDevice = await devices.GetByIdAsNoTrackingAsync(endpoint.DeviceId, ct);
                if (remoteDevice is not null)
                {
                    syncIdentities.TryAdd(remoteDevice);
                    var authenticatedEndpoint = new DiscoveredDeviceEndpoint
                    {
                        Host = endpoint.Host,
                        Port = endpoint.Port,
                        TlsCertFingerprint = endpoint.TlsCertFingerprint
                    };
                    _endpointRegistry.AddOrUpdate(authenticatedEndpoint);
                    _presenceRegistry?.RefreshAuthenticated(
                        remoteDevice.TlsCertFingerprint,
                        authenticatedEndpoint,
                        DevicePresenceObservationSource.Enrollment);
                }
                try { await pendingSyncActivation.ActivatePendingAsync(CancellationToken.None); } catch { }
            }
        }
        catch (DeviceEnrollmentPartiallyCommittedException)
        {
            throw;
        }
        catch (DeviceEnrollmentException ex)
        {
            if (!authoritativeCommitCompleted)
                throw;

            await TryRecordEnrollmentTransferFailureAsync(commitId, transferCompleted, ex.Message);
            throw CreatePartialCommitException(
                commitId,
                endpoint,
                ex.ErrorCode,
                ex.Message,
                transferPending: !transferCompleted,
                ex);
        }
        catch (Exception ex)
        {
            if (!authoritativeCommitCompleted)
            {
                throw new DeviceEnrollmentException(
                    DeviceEnrollmentErrorCode.Unknown,
                    ex.Message,
                    isKnownNotCommitted: !authoritativeCommitAttempted,
                    innerException: ex);
            }

            await TryRecordEnrollmentTransferFailureAsync(commitId, transferCompleted, ex.Message);
            throw CreatePartialCommitException(
                commitId,
                endpoint,
                DeviceEnrollmentErrorCode.Unknown,
                ex.Message,
                transferPending: !transferCompleted,
                ex);
        }
    }


    private async Task TryRecordEnrollmentTransferFailureAsync(
        Guid commitId,
        bool transferCompleted,
        string? error)
    {
        try
        {
            await UpdateEnrollmentCommitAfterTransferAsync(commitId, transferCompleted, error, CancellationToken.None);
        }
        catch
        {
        }
    }

    private DeviceEnrollmentPartiallyCommittedException CreatePartialCommitException(
        DeviceEnrollmentCommit commit,
        DeviceEnrollmentErrorCode errorCode,
        string message,
        bool transferPending,
        Exception? innerException = null) =>
        new(
            errorCode,
            message,
            commit.CommitId,
            commit.TargetDeviceId,
            commit.TargetOriginInstanceId,
            recoveryAvailable: true,
            transferPending,
            requiresSignedRemovalToUndo: true,
            requiresProcessRestart: errorCode == DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
            innerException);

    private DeviceEnrollmentPartiallyCommittedException CreatePartialCommitException(
        Guid commitId,
        EnrollmentEndpoint endpoint,
        DeviceEnrollmentErrorCode errorCode,
        string message,
        bool transferPending,
        Exception? innerException = null) =>
        new(
            errorCode,
            message,
            commitId,
            endpoint.DeviceId,
            endpoint.OriginInstanceId,
            recoveryAvailable: true,
            transferPending,
            requiresSignedRemovalToUndo: true,
            requiresProcessRestart: errorCode == DeviceEnrollmentErrorCode.UnsupportedDatabaseVersion,
            innerException);


    private void ValidateRecoverableEnrollmentCommit(DeviceEnrollmentCommit commit, EnrollmentEndpoint endpoint)
    {
        if (commit.TargetDeviceId != endpoint.DeviceId || commit.TargetOriginInstanceId != endpoint.OriginInstanceId ||
            !Hashing.Verify(commit.TargetSignPublicKeyHash, Hashing.SHA256Hash(endpoint.SignPublicKey)) ||
            !Hashing.Verify(commit.TargetAgreementPublicKeyHash, Hashing.SHA256Hash(endpoint.AgreementPublicKey)) ||
            !string.Equals(SyncIdentityUtil.NormalizeFingerprint(commit.TargetTlsCertFingerprint), SyncIdentityUtil.NormalizeFingerprint(endpoint.TlsCertFingerprint), StringComparison.OrdinalIgnoreCase) ||
            commit.TargetDeviceType != endpoint.DeviceType)
            throw new DeviceEnrollmentException(DeviceEnrollmentErrorCode.DeviceIdentityConflict, "The pending enrollment belongs to a different target installation identity.");
    }

    private async Task UpdateEnrollmentCommitAfterTransferAsync(Guid commitId, bool succeeded, string? error, CancellationToken ct)
    {
        if (commitId == Guid.Empty)
            return;
        using var scope = _scopeFactory.CreateScope();
        var commits = scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentCommitRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var commit = await commits.GetByIdAsync(commitId, ct);
        if (commit is null)
            return;
        commit.Status = succeeded ? DeviceEnrollmentCommitStatus.Transferred : DeviceEnrollmentCommitStatus.TransferFailed;
        commit.LastAttemptAtUtc = DateTimeOffset.UtcNow;
        commit.LastError = succeeded ? null : (string.IsNullOrWhiteSpace(error) ? "Enrollment transfer failed." : error);
        commit.CompletedAtUtc = succeeded ? DateTimeOffset.UtcNow : null;
        commit.Version = checked(commit.Version + 1);
        commits.Update(commit);
        await unitOfWork.SaveChangesAsync(ct);
    }


    public async Task<DeviceEnrollmentInfoResponse> GetIncomingEnrollmentInfoAsync(string sessionId, byte[] codeProof, CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        DeviceEnrollmentInfoResponse response;
        var endTemporaryMode = false;

        lock (_lock)
        {
            ExpireSessionIfNeededLocked();
            var session = _currentSession;

            if (session is null || session.State != DeviceEnrollmentState.Waiting)
            {
                endTemporaryMode = session?.State is DeviceEnrollmentState.Expired or DeviceEnrollmentState.Failed or DeviceEnrollmentState.Completed;
                response = new DeviceEnrollmentInfoResponse
                {
                    Ok = false,
                    ErrorCode = session?.ErrorCode ?? DeviceEnrollmentErrorCode.NewDeviceRejected,
                    Error = session?.ErrorMessage ?? "No active enrollment session was found."
                };
            }
            else if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
            {
                var failure = RegisterFailedEnrollmentValidationLocked(
                    session,
                    DeviceEnrollmentErrorCode.CodeProofInvalid,
                    "The enrollment session does not match the displayed code.");
                endTemporaryMode = failure.LimitReached;
                response = new DeviceEnrollmentInfoResponse
                {
                    Ok = false,
                    ErrorCode = DeviceEnrollmentErrorCode.CodeProofInvalid,
                    Error = failure.Message
                };
            }
            else
            {
                var expectedProof = DeviceEnrollmentCode.BuildEnrollmentInfoProof(sessionId, session.Secret);
                if (!DeviceEnrollmentCode.FixedTimeEquals(expectedProof, codeProof))
                {
                    var failure = RegisterFailedEnrollmentValidationLocked(
                        session,
                        DeviceEnrollmentErrorCode.CodeProofInvalid,
                        "The enrollment code proof is invalid.");
                    endTemporaryMode = failure.LimitReached;
                    response = new DeviceEnrollmentInfoResponse
                    {
                        Ok = false,
                        ErrorCode = DeviceEnrollmentErrorCode.CodeProofInvalid,
                        Error = failure.Message
                    };
                }
                else
                {
                    response = new DeviceEnrollmentInfoResponse
                    {
                        Ok = true,
                        DeviceId = _identity.LocalDeviceId,
                        OriginInstanceId = _identity.OriginInstanceId,
                        DeviceType = _identity.DeviceType,
                        TlsCertFingerprint = _identity.FingerprintHex,
                        SignPublicKey = _identity.SignPublicKey,
                        AgreementPublicKey = _identity.AgreementPublicKey
                    };
                }
            }
        }

        if (endTemporaryMode)
            await _syncRuntime.EndEnrollmentOnlyAsync(CancellationToken.None);

        return response;
    }


    public async Task<string> RegisterIncomingEnrollmentValidationFailureAsync(
        DeviceEnrollmentErrorCode errorCode,
        string message,
        CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        return await RegisterIncomingEnrollmentValidationFailureCoreAsync(errorCode, message, ct);
    }


    private async Task<string> RegisterIncomingEnrollmentValidationFailureCoreAsync(
        DeviceEnrollmentErrorCode errorCode,
        string message,
        CancellationToken ct)
    {
        var endEnrollmentOnlyMode = false;
        var responseMessage = message;

        lock (_lock)
        {
            ExpireSessionIfNeededLocked();
            var current = _currentSession;

            if (current is not null && current.State == DeviceEnrollmentState.Waiting)
            {
                var failure = RegisterFailedEnrollmentValidationLocked(current, errorCode, message);
                endEnrollmentOnlyMode = failure.LimitReached;
                responseMessage = failure.Message;
            }
            else if (current?.ErrorMessage is not null)
            {
                responseMessage = current.ErrorMessage;
            }
        }

        if (endEnrollmentOnlyMode)
            await _syncRuntime.EndEnrollmentOnlyAsync(CancellationToken.None);

        return responseMessage;
    }


    public async Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> CompleteIncomingEnrollmentAsync(
        string sessionId,
        byte[] codeProof,
        byte[] snapshotBytes,
        string sourceDeviceId,
        Guid sourceOriginInstanceId,
        byte[] sourceSignPublicKey,
        string sourceTlsCertFingerprint,
        string actualClientTlsCertFingerprint,
        string? sourceHost,
        Guid targetDeviceId,
        Guid targetOriginInstanceId,
        int snapshotEncryptionVersion,
        byte[] snapshotEncryptionNonce,
        byte[] snapshotEncryptionTag,
        CancellationToken ct = default)
    {
        using var enrollmentOperation = EnterEnrollmentOperation();
        using var preCriticalCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            enrollmentOperation.AdmissionCancellationToken);
        var preCriticalToken = preCriticalCancellation.Token;
        preCriticalToken.ThrowIfCancellationRequested();
        EnrollmentSession? session;
        (DeviceEnrollmentErrorCode Code, string Message)? earlyFailure = null;
        var endTemporaryMode = false;

        lock (_lock)
        {
            ExpireSessionIfNeededLocked();
            session = _currentSession;

            if (session is null || session.State != DeviceEnrollmentState.Waiting)
            {
                earlyFailure = (
                    session?.ErrorCode ?? DeviceEnrollmentErrorCode.NewDeviceRejected,
                    session?.ErrorMessage ?? "No active enrollment session was found.");
                endTemporaryMode = session?.State is DeviceEnrollmentState.Expired or DeviceEnrollmentState.Failed or DeviceEnrollmentState.Completed;
            }
            else if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
            {
                var failure = RegisterFailedEnrollmentValidationLocked(
                    session,
                    DeviceEnrollmentErrorCode.CodeProofInvalid,
                    "The enrollment session does not match the displayed code.");
                earlyFailure = (DeviceEnrollmentErrorCode.CodeProofInvalid, failure.Message);
                endTemporaryMode = failure.LimitReached;
            }
        }

        if (earlyFailure is not null)
        {
            if (endTemporaryMode)
                await _syncRuntime.EndEnrollmentOnlyAsync(CancellationToken.None);
            return (false, earlyFailure.Value.Code, earlyFailure.Value.Message);
        }

        var activeSession = session!;

        if (targetDeviceId != _identity.LocalDeviceId || targetOriginInstanceId != _identity.OriginInstanceId)
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.NewDeviceRejected, "The enrollment transfer targets a different local installation.");

        if (sourceOriginInstanceId == Guid.Empty)
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.ProfileDataInvalid, "The source installation origin is missing.");

        if (!string.Equals(FingerprintUtil.Normalize(sourceTlsCertFingerprint), FingerprintUtil.Normalize(actualClientTlsCertFingerprint), StringComparison.OrdinalIgnoreCase))
        {
            BackendDebugLog.Error($"Incoming enrollment rejected because client TLS fingerprint did not match. Expected={FingerprintUtil.Normalize(sourceTlsCertFingerprint)}, Actual={FingerprintUtil.Normalize(actualClientTlsCertFingerprint)}.");
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.NewDeviceRejected, "The source device TLS certificate does not match the enrollment request.");
        }

        var expectedProof = DeviceEnrollmentCode.BuildCompletionProof(
            sessionId,
            activeSession.Secret,
            sourceDeviceId,
            sourceOriginInstanceId,
            sourceSignPublicKey,
            sourceTlsCertFingerprint,
            targetDeviceId,
            targetOriginInstanceId);

        if (!DeviceEnrollmentCode.FixedTimeEquals(expectedProof, codeProof))
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.CodeProofInvalid, "The enrollment code proof is invalid.");

        preCriticalToken.ThrowIfCancellationRequested();

        DeviceEnrollmentSnapshot snapshot;
        try
        {
            snapshot = _snapshotService.DecryptAndDeserialize(
                sessionId,
                activeSession.Secret,
                snapshotBytes,
                sourceDeviceId,
                sourceOriginInstanceId,
                sourceSignPublicKey,
                sourceTlsCertFingerprint,
                targetDeviceId,
                targetOriginInstanceId,
                snapshotEncryptionVersion,
                snapshotEncryptionNonce,
                snapshotEncryptionTag);
        }
        catch (CryptographicException ex)
        {
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.ProfileDataInvalid, $"The encrypted enrollment snapshot could not be authenticated: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.ProfileDataInvalid, ex.Message);
        }

        preCriticalToken.ThrowIfCancellationRequested();
        enrollmentOperation.EnterCriticalSection();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            BackendDebugLog.Info($"Importing incoming enrollment snapshot. Users={snapshot.Users.Count}, Groups={snapshot.Groups.Count}, Devices={snapshot.Devices.Count}, UserDevices={snapshot.UserDevices.Count}.");
            // The importer performs stale-barrier and exact-idempotence checks. A partially imported
            // bootstrap must be retryable with the same immutable addition operation. Serialize the
            // bootstrap with deletion, merge, publication, and lifecycle operations for this user id.
            var lifecycle = scope.ServiceProvider.GetRequiredService<IUserLifecycleCoordinator>();
            await lifecycle.ExecuteAsync(
                snapshot.PrimaryUserId,
                token => _snapshotImporter.ImportAsync(scope.ServiceProvider, snapshot, token),
                ct);
            await _syncRuntime.RefreshSyncEnabledAsync(ct);
            await _registrationService.RegisterIncomingEnrollmentSourceEndpointAsync(scope.ServiceProvider, sourceDeviceId, sourceTlsCertFingerprint, sourceHost, ct);
            BackendDebugLog.Info("Incoming enrollment snapshot import completed successfully.");
        }
        catch (DeviceEnrollmentException ex)
        {
            BackendDebugLog.Error($"Incoming enrollment snapshot import rejected: {ex.Message}", ex);
            return await RejectIncomingValidationAsync(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            BackendDebugLog.Error($"Incoming enrollment snapshot import failed: {ex.Message}", ex);
            return await RejectIncomingValidationAsync(DeviceEnrollmentErrorCode.ProfileDataInvalid, ex.Message);
        }

        lock (_lock)
        {
            if (_currentSession is not null)
            {
                _currentSession.State = DeviceEnrollmentState.Completed;
                _currentSession.ErrorCode = DeviceEnrollmentErrorCode.Unknown;
                _currentSession.ErrorMessage = null;
                CancelEnrollmentExpirationLocked();
                DeactivateEnrollmentDiscoveryLocked();
                _currentSession.ClearSensitiveData();
            }
        }

        await _syncRuntime.EndEnrollmentOnlyAsync(ct);
        return (true, DeviceEnrollmentErrorCode.Unknown, null);

        async Task<(bool Ok, DeviceEnrollmentErrorCode ErrorCode, string? Error)> RejectIncomingValidationAsync(DeviceEnrollmentErrorCode errorCode, string message)
        {
            var responseMessage = await RegisterIncomingEnrollmentValidationFailureCoreAsync(errorCode, message, CancellationToken.None);
            return (false, errorCode, responseMessage);
        }
    }


    private (bool LimitReached, string Message) RegisterFailedEnrollmentValidationLocked(
        EnrollmentSession session,
        DeviceEnrollmentErrorCode errorCode,
        string message)
    {
        session.FailedValidationAttempts++;
        var remainingAttempts = Math.Max(0, SyncConstants.MaxEnrollmentValidationAttempts - session.FailedValidationAttempts);

        if (remainingAttempts == 0)
        {
            session.State = DeviceEnrollmentState.Failed;
            session.ErrorCode = errorCode;
            session.ErrorMessage = "Enrollment was stopped after three failed validation attempts. Generate a new enrollment code before trying again.";
            CancelEnrollmentExpirationLocked();
            DeactivateEnrollmentDiscoveryLocked();
            session.ClearSensitiveData();
            return (true, session.ErrorMessage);
        }

        var suffix = remainingAttempts == 1
            ? "1 validation attempt remains."
            : $"{remainingAttempts} validation attempts remain.";
        return (false, $"{message} {suffix}");
    }


    internal DeviceEnrollmentOperationLease EnterEnrollmentOperation()
    {
        lock (_lock)
        {
            if (!_interactiveAdmissionOpen || !_enrollmentAvailability.IsEnrollmentAllowed)
            {
                throw new DeviceEnrollmentException(
                    DeviceEnrollmentErrorCode.InteractiveSessionRequired,
                    "Device enrollment requires an active interactive session.");
            }

            var admissionCancellation = _interactiveAdmissionCancellation
                ?? throw new InvalidOperationException("Enrollment admission cancellation is unavailable.");
            _activeEnrollmentOperations++;
            return new DeviceEnrollmentOperationLease(this, admissionCancellation.Token);
        }
    }


    internal void EnterCriticalEnrollmentSection()
    {
        lock (_lock)
        {
            if (!_interactiveAdmissionOpen ||
                !_enrollmentAvailability.IsEnrollmentAllowed ||
                _interactiveAdmissionCancellation?.IsCancellationRequested != false)
            {
                throw new DeviceEnrollmentException(
                    DeviceEnrollmentErrorCode.InteractiveSessionRequired,
                    "Device enrollment requires an active interactive session.");
            }
        }
    }


    internal void CompleteEnrollmentOperation()
    {
        TaskCompletionSource? operationsDrained = null;
        lock (_lock)
        {
            if (_activeEnrollmentOperations == 0)
                throw new InvalidOperationException("No enrollment operation is active.");

            _activeEnrollmentOperations--;
            if (!_interactiveAdmissionOpen && _activeEnrollmentOperations == 0)
            {
                operationsDrained = _enrollmentOperationsDrained;
                _enrollmentOperationsDrained = null;
            }
        }

        operationsDrained?.TrySetResult();
    }


    private async Task CancelEnrollmentCoreAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            CancelEnrollmentExpirationLocked();
            DeactivateEnrollmentDiscoveryLocked();
            _currentSession?.ClearSensitiveData();
            _currentSession = null;
        }

        await _syncRuntime.EndEnrollmentOnlyAsync(cancellationToken);
    }


    private void StartEnrollmentExpirationCountdownLocked(EnrollmentSession session)
    {
        CancelEnrollmentExpirationLocked();
        var cancellation = new CancellationTokenSource();
        _enrollmentExpirationCancellation = cancellation;
        _ = ExpireEnrollmentSessionAsync(session.SessionId, session.ExpiresAt, cancellation);
    }


    private async Task ExpireEnrollmentSessionAsync(string sessionId, DateTimeOffset expiresAt, CancellationTokenSource cancellation)
    {
        try
        {
            var ct = cancellation.Token;
            var delay = expiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            var expired = false;
            lock (_lock)
            {
                if (!ct.IsCancellationRequested &&
                    _currentSession is not null &&
                    string.Equals(_currentSession.SessionId, sessionId, StringComparison.Ordinal) &&
                    _currentSession.State == DeviceEnrollmentState.Waiting)
                {
                    _currentSession.State = DeviceEnrollmentState.Expired;
                    _currentSession.ErrorCode = DeviceEnrollmentErrorCode.CodeExpired;
                    _currentSession.ErrorMessage = "The enrollment code expired.";
                    _enrollmentExpirationCancellation = null;
                    DeactivateEnrollmentDiscoveryLocked();
                    _currentSession.ClearSensitiveData();
                    expired = true;
                }
            }

            if (expired)
                await _syncRuntime.EndEnrollmentOnlyAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }


    private void CancelEnrollmentExpirationLocked()
    {
        var cancellation = _enrollmentExpirationCancellation;
        _enrollmentExpirationCancellation = null;
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }


    private void DeactivateEnrollmentDiscoveryLocked()
    {
        if (_currentSession is not null)
            _localDiscovery.DeactivateEnrollmentSession(_currentSession.SessionId);
    }


    private void ExpireSessionIfNeededLocked()
    {
        if (_currentSession is null || _currentSession.State != DeviceEnrollmentState.Waiting)
            return;

        if (DateTimeOffset.UtcNow <= _currentSession.ExpiresAt)
            return;

        _currentSession.State = DeviceEnrollmentState.Expired;
        _currentSession.ErrorCode = DeviceEnrollmentErrorCode.CodeExpired;
        _currentSession.ErrorMessage = "The enrollment code expired.";
        CancelEnrollmentExpirationLocked();
        DeactivateEnrollmentDiscoveryLocked();
        _currentSession.ClearSensitiveData();
    }


    public void Dispose()
    {
        CancellationTokenSource? admissionCancellation;
        lock (_lock)
        {
            _interactiveAdmissionOpen = false;
            admissionCancellation = _interactiveAdmissionCancellation;
            _interactiveAdmissionCancellation = null;
            CancelEnrollmentExpirationLocked();
            DeactivateEnrollmentDiscoveryLocked();
            _currentSession?.ClearSensitiveData();
            _currentSession = null;
        }

        admissionCancellation?.Cancel();
        admissionCancellation?.Dispose();
    }
}
