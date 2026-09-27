using Microsoft.Extensions.DependencyInjection;
using PasswordManagerLocal.Common.Backend.Abstractions.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Abstractions.Repositories;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Services.Hosted;
using PasswordManagerLocal.Common.Backend.State;
using PasswordManagerLocal.Common.Backend.Sync;
using PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;
using PasswordManagerLocal.Common.Tests.Fakes;
using System.Net;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

using PasswordManagerLocal.Common.Backend.Sync.Enrollment;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class LocalDiscoveryHostedServiceSecurityTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncQuery_FromTrustedSignedDevice_GetsSignedResponse()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(localIdentity, syncIdentities, transport, new EnrollmentRuntimeState());

        await service.StartAsync();

        var nonce = Enumerable.Repeat((byte)0x11, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            remoteDevice.Id);
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(payload);

        MSTestAssert.AreEqual(1, transport.UnicastPayloads.Count);
        var responsePayload = transport.UnicastPayloads[0].Payload;
        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeSyncResponse(responsePayload, out var response));
        MSTestAssert.AreEqual(remoteDevice.Id, response.RequesterDeviceId);
        MSTestAssert.AreEqual(localIdentity.LocalDeviceId, response.ResponderDeviceId);
        CollectionAssert.AreEqual(nonce, response.QueryNonce);
        MSTestAssert.AreEqual(IPAddress.Parse("192.168.1.10"), response.ResponderAddress);
        MSTestAssert.IsTrue(LocalDiscoveryAuthenticator.VerifySignature(localIdentity.SignPublicKey, response.AuthenticatedBytes, response.Signature));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncQuery_FromTrustedPendingDevice_StartsDeliveryToObservedSource()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        var syncTasks = new FakeDeviceSyncTaskService();
        var networkAddresses = new FakeLocalNetworkAddressService
        {
            RemoteEndpointPriorityHandler = host => host == "192.168.1.77" ? 5000 : int.MinValue
        };
        using var service = CreateService(
            localIdentity,
            syncIdentities,
            transport,
            new EnrollmentRuntimeState(),
            syncTasks,
            networkAddresses);

        await service.StartAsync();

        var nonce = Enumerable.Repeat((byte)0x12, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            remoteDevice.Id);
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(
            authenticatedBytes,
            signature,
            SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(payload, "192.168.1.77");

        MSTestAssert.AreEqual(1, syncTasks.Starts.Count);
        MSTestAssert.AreEqual(remoteDevice.Id, syncTasks.Starts[0].Device.Id);
        MSTestAssert.AreEqual("192.168.1.77", syncTasks.Starts[0].Endpoint.Host);
        MSTestAssert.AreEqual(SyncConstants.SyncPort, syncTasks.Starts[0].Endpoint.Port);
        MSTestAssert.AreEqual(remoteDevice.TlsCertFingerprint, syncTasks.Starts[0].Endpoint.TlsCertFingerprint);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncQuery_FromEligibleDeviceWithoutPendingWork_IsObservedWithoutStartingDelivery()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        remoteDevice.GenerateIntegrityHash();

        var userId = Guid.NewGuid();
        var localUsers = new FakeLocalUserDeviceRepository();
        var userDevices = new FakeUserDeviceRepository();
        var localLink = new LocalUserDevice
        {
            UserId = userId,
            LocalDeviceIdentityId = localIdentity.LocalDeviceId,
            IsSyncOn = true
        };
        localLink.GenerateIntegrityHash();
        await localUsers.AddAsync(localLink);

        var remoteLink = new UserDevice
        {
            UserId = userId,
            DeviceId = remoteDevice.Id,
            Device = remoteDevice,
            IsSyncOn = true,
            IsDeleted = false
        };
        remoteLink.GenerateIntegrityHash();
        await userDevices.AddAsync(remoteLink);

        var services = new ServiceCollection();
        services.AddSingleton<ILocalUserDeviceRepository>(localUsers);
        services.AddSingleton<IUserDeviceRepository>(userDevices);
        using var provider = services.BuildServiceProvider();

        var syncIdentities = new FakeSyncDeviceIdentityService();
        var endpointRegistry = new DiscoveredDeviceEndpointRegistry();
        var syncTasks = new FakeDeviceSyncTaskService();
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(
            localIdentity,
            syncIdentities,
            transport,
            new EnrollmentRuntimeState(),
            syncTasks,
            endpointRegistry: endpointRegistry,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        await service.StartAsync();

        var nonce = Enumerable.Repeat((byte)0x13, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            remoteDevice.Id);
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(
            authenticatedBytes,
            signature,
            SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(payload, "192.168.1.77");

        MSTestAssert.AreEqual(1, transport.UnicastPayloads.Count);
        MSTestAssert.AreEqual(0, syncTasks.Starts.Count);
        MSTestAssert.IsTrue(endpointRegistry.IsRecentlyDiscovered(
            remoteDevice.TlsCertFingerprint,
            TimeSpan.FromSeconds(35)));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncQuery_WithInvalidSignature_IsSilentlyIgnored()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(localIdentity, syncIdentities, transport, new EnrollmentRuntimeState());

        await service.StartAsync();

        var nonce = Enumerable.Repeat((byte)0x22, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            remoteDevice.Id);
        var invalidSignature = new byte[SyncConstants.LocalDiscoverySignatureBytes];
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, invalidSignature, SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(payload);

        MSTestAssert.AreEqual(0, transport.UnicastPayloads.Count);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncQuery_ReplayedAuthenticatedNonce_GetsOnlyOneResponse()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(localIdentity, syncIdentities, transport, new EnrollmentRuntimeState());

        await service.StartAsync();

        var nonce = Enumerable.Repeat((byte)0x23, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            remoteDevice.Id);
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(payload);
        await Task.Delay(SyncConstants.LocalDiscoveryRequestThrottleMilliseconds + 25);
        await transport.InjectAsync(payload);

        var freshNonce = Enumerable.Repeat((byte)0x24, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var freshAuthenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            freshNonce,
            remoteDevice.Id);
        var freshSignature = SignatureAlgorithm.Ed25519.Sign(remoteKey, freshAuthenticatedBytes);
        var freshPayload = LocalDiscoveryPacketCodec.AppendAuthenticator(
            freshAuthenticatedBytes,
            freshSignature,
            SyncConstants.LocalDiscoverySignatureBytes);
        await transport.InjectAsync(freshPayload);

        MSTestAssert.AreEqual(2, transport.UnicastPayloads.Count);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncResponse_RequiresOutstandingNonce_UsesObservedSource_AndIgnoresReplay()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        var syncTasks = new FakeDeviceSyncTaskService();
        var networkAddresses = new FakeLocalNetworkAddressService
        {
            RemoteEndpointPriorityHandler = host => host == "192.168.1.50" ? 5000 : int.MinValue
        };
        using var service = CreateService(
            localIdentity,
            syncIdentities,
            transport,
            new EnrollmentRuntimeState(),
            syncTasks,
            networkAddresses);

        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count > 0);

        var queryPayload = transport.MulticastPayloads[0];
        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeSyncQuery(queryPayload, out var query));

        var fingerprint = Convert.FromHexString(remoteDevice.TlsCertFingerprint);
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncResponseAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            query.Nonce,
            localIdentity.LocalDeviceId,
            remoteDevice.Id,
            fingerprint,
            IPAddress.Parse("192.168.1.50"));
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var response = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(response, "192.168.1.50");
        await transport.InjectAsync(response, "192.168.1.50");

        MSTestAssert.AreEqual(1, syncTasks.Starts.Count);
        MSTestAssert.AreEqual("192.168.1.50", syncTasks.Starts[0].Endpoint.Host);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task SyncResponse_AuthenticatedAddressMustMatchObservedUdpSource()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        var syncTasks = new FakeDeviceSyncTaskService();
        using var service = CreateService(localIdentity, syncIdentities, transport, new EnrollmentRuntimeState(), syncTasks);

        await service.StartAsync();
        await WaitForAsync(() => transport.MulticastPayloads.Count > 0);
        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeSyncQuery(transport.MulticastPayloads[0], out var query));

        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncResponseAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            query.Nonce,
            localIdentity.LocalDeviceId,
            remoteDevice.Id,
            Convert.FromHexString(remoteDevice.TlsCertFingerprint),
            IPAddress.Parse("192.168.1.50"));
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var response = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(response, "192.168.1.51");

        MSTestAssert.AreEqual(0, syncTasks.Starts.Count);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task UnauthenticatedFlood_DoesNotSuppressTrustedSignedQuery()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        using var remoteKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: true);
        var remoteDevice = CreateTrustedDevice(remoteKey);
        var syncIdentities = new FakeSyncDeviceIdentityService();
        syncIdentities.TryAdd(remoteDevice);
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(localIdentity, syncIdentities, transport, new EnrollmentRuntimeState());

        await service.StartAsync();

        for (var i = 0; i < 256; i++)
            await transport.InjectAsync([0x00]);

        var nonce = Enumerable.Repeat((byte)0x6B, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            remoteDevice.Id);
        var signature = SignatureAlgorithm.Ed25519.Sign(remoteKey, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        await transport.InjectAsync(payload);

        MSTestAssert.AreEqual(1, transport.UnicastPayloads.Count);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task EnrollmentResponse_AuthenticatedAddressMustMatchObservedUdpSource()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: false);
        var enrollmentState = new EnrollmentRuntimeState();
        enrollmentState.Activate();
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(localIdentity, new FakeSyncDeviceIdentityService(), transport, enrollmentState);
        var secret = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var parsed = new DeviceEnrollmentParsedCode
        {
            SessionId = "ABCDEFG2",
            Secret = secret
        };

        await service.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var discoveryTask = service.FindEnrollmentEndpointsAsync(parsed, timeout.Token);

        await WaitForAsync(() => transport.MulticastPayloads.Count > 0);
        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeEnrollmentQuery(transport.MulticastPayloads[0], out var query));

        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentResponseAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            query.Nonce,
            parsed.SessionId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DeviceType.WindowsPc,
            Enumerable.Repeat((byte)0x44, 32).ToArray(),
            Enumerable.Repeat((byte)0x33, SyncConstants.SyncDeltaEd25519PublicKeyBytes).ToArray(),
            Enumerable.Repeat((byte)0x55, SyncConstants.SyncDeltaX25519PublicKeyBytes).ToArray(),
            IPAddress.Parse("192.168.1.50"));
        var mac = LocalDiscoveryAuthenticator.ComputeMac(secret, authenticatedBytes);
        var response = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, SyncConstants.LocalDiscoveryMacBytes);

        await transport.InjectAsync(response, "192.168.1.51");
        MSTestAssert.IsFalse(discoveryTask.IsCompleted);

        await transport.InjectAsync(response, "192.168.1.50");
        var endpoints = await discoveryTask;

        MSTestAssert.AreEqual(1, endpoints.Count);
        MSTestAssert.AreEqual("192.168.1.50", endpoints[0].Host);
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public async Task EnrollmentQuery_RequiresCorrectSessionSecret()
    {
        using var localKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var localIdentity = CreateIdentity(localKey, isSyncOn: false);
        var enrollmentState = new EnrollmentRuntimeState();
        enrollmentState.Activate();
        var transport = new FakeLocalDiscoveryTransport();
        using var service = CreateService(localIdentity, new FakeSyncDeviceIdentityService(), transport, enrollmentState);
        var secret = Enumerable.Repeat((byte)0x5A, 16).ToArray();

        await service.StartAsync();
        service.ActivateEnrollmentSession("ABCDEFG2", secret, DateTimeOffset.UtcNow.AddMinutes(10));

        var wrongQuery = BuildEnrollmentQuery("ABCDEFG2", Enumerable.Repeat((byte)0x41, 16).ToArray(), 0x31);
        await transport.InjectAsync(wrongQuery, "192.168.1.52");
        MSTestAssert.AreEqual(0, transport.UnicastPayloads.Count);

        var correctQuery = BuildEnrollmentQuery("ABCDEFG2", secret, 0x32);
        await transport.InjectAsync(correctQuery, "192.168.1.52");

        MSTestAssert.AreEqual(1, transport.UnicastPayloads.Count);
        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeEnrollmentResponse(transport.UnicastPayloads[0].Payload, out var response));
        MSTestAssert.IsTrue(LocalDiscoveryAuthenticator.VerifyMac(secret, response.AuthenticatedBytes, response.Mac));
        MSTestAssert.AreEqual(localIdentity.LocalDeviceId, response.DeviceId);
    }


    private static LocalDiscoveryHostedService CreateService(
        FakeDeviceIdentityService identity,
        FakeSyncDeviceIdentityService syncIdentities,
        FakeLocalDiscoveryTransport transport,
        EnrollmentRuntimeState enrollmentState,
        FakeDeviceSyncTaskService? syncTasks = null,
        FakeLocalNetworkAddressService? networkAddresses = null,
        DiscoveredDeviceEndpointRegistry? endpointRegistry = null,
        IServiceScopeFactory? scopeFactory = null) =>
        new(
            identity,
            syncIdentities,
            endpointRegistry ?? new DiscoveredDeviceEndpointRegistry(),
            syncTasks ?? new FakeDeviceSyncTaskService(),
            enrollmentState,
            networkAddresses ?? new FakeLocalNetworkAddressService(),
            transport,
            new FakeLocalDiscoveryNetworkLease(),
            CreateInteractiveProfileProvider(),
            scopeFactory);


    private static FakeBackendExecutionProfileProvider CreateInteractiveProfileProvider()
    {
        var provider = new FakeBackendExecutionProfileProvider();
        provider.SetProfile(
            new BackendExecutionProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(35)),
            isInteractive: true);
        return provider;
    }


    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                MSTestAssert.Fail("The expected asynchronous discovery action did not occur.");

            await Task.Delay(10);
        }
    }


    private static FakeDeviceIdentityService CreateIdentity(Key signingKey, bool isSyncOn)
    {
        var publicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return new FakeDeviceIdentityService
        {
            IsSyncOn = isSyncOn,
            LocalDeviceId = Guid.NewGuid(),
            SignPublicKey = publicKey,
            AgreementPublicKey = Enumerable.Repeat((byte)0x77, SyncConstants.SyncDeltaX25519PublicKeyBytes).ToArray(),
            FingerprintHex = Convert.ToHexString(Enumerable.Repeat((byte)0x66, 32).ToArray()),
            SignHandler = data => SignatureAlgorithm.Ed25519.Sign(signingKey, data)
        };
    }


    private static Device CreateTrustedDevice(Key signingKey) =>
        new()
        {
            Id = Guid.NewGuid(),
            IsTrusted = true,
            IsBlocked = false,
            SignPublicKey = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey),
            PublicKey = Enumerable.Repeat((byte)0x55, SyncConstants.SyncDeltaX25519PublicKeyBytes).ToArray(),
            TlsCertFingerprint = Convert.ToHexString(Enumerable.Repeat((byte)0x44, 32).ToArray())
        };


    private static byte[] BuildEnrollmentQuery(string sessionId, byte[] secret, byte nonceValue)
    {
        var nonce = Enumerable.Repeat(nonceValue, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentQueryAuthenticatedBytes(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            nonce,
            sessionId);
        var mac = LocalDiscoveryAuthenticator.ComputeMac(secret, authenticatedBytes);
        return LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, SyncConstants.LocalDiscoveryMacBytes);
    }
}
