using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSec.Cryptography;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Backend.Models;
using PasswordManagerLocal.Common.Backend.Sync.Discovery;
using PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;
using System.Net;

using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace PasswordManagerLocal.Common.Tests.Backend.Sync.Discovery;

[TestClass]
public sealed class LocalDiscoveryPacketSecurityTests
{
    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void SyncQuery_RoundTripsAndVerifiesSignature()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var nonce = Enumerable.Range(0, SyncConstants.LocalDiscoveryNonceBytes).Select(value => (byte)value).ToArray();
        var deviceId = Guid.NewGuid();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(123456789, nonce, deviceId);
        var signature = SignatureAlgorithm.Ed25519.Sign(key, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeSyncQuery(payload, out var decoded));
        MSTestAssert.AreEqual(123456789L, decoded.UnixTimeSeconds);
        MSTestAssert.AreEqual(deviceId, decoded.RequesterDeviceId);
        CollectionAssert.AreEqual(nonce, decoded.Nonce);

        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        MSTestAssert.IsTrue(LocalDiscoveryAuthenticator.VerifySignature(publicKey, decoded.AuthenticatedBytes, decoded.Signature));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void SyncQuery_TamperedAuthenticatedBytes_FailSignatureVerification()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters());
        var nonce = new byte[SyncConstants.LocalDiscoveryNonceBytes];
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildSyncQueryAuthenticatedBytes(123456789, nonce, Guid.NewGuid());
        var signature = SignatureAlgorithm.Ed25519.Sign(key, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, signature, SyncConstants.LocalDiscoverySignatureBytes);

        payload[10] ^= 0x01;

        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeSyncQuery(payload, out var decoded));
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        MSTestAssert.IsFalse(LocalDiscoveryAuthenticator.VerifySignature(publicKey, decoded.AuthenticatedBytes, decoded.Signature));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void EnrollmentQuery_WrongSecret_FailsAuthentication()
    {
        var secret = Enumerable.Repeat((byte)0x41, 16).ToArray();
        var wrongSecret = Enumerable.Repeat((byte)0x42, 16).ToArray();
        var nonce = Enumerable.Repeat((byte)0x11, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentQueryAuthenticatedBytes(123456789, nonce, "ABCDEFG2");
        var mac = LocalDiscoveryAuthenticator.ComputeMac(secret, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, SyncConstants.LocalDiscoveryMacBytes);

        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeEnrollmentQuery(payload, out var decoded));
        MSTestAssert.IsTrue(LocalDiscoveryAuthenticator.VerifyMac(secret, decoded.AuthenticatedBytes, decoded.Mac));
        MSTestAssert.IsFalse(LocalDiscoveryAuthenticator.VerifyMac(wrongSecret, decoded.AuthenticatedBytes, decoded.Mac));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void EnrollmentResponse_RoundTripsAuthenticatedIdentity()
    {
        var secret = Enumerable.Repeat((byte)0x5A, 16).ToArray();
        var nonce = Enumerable.Repeat((byte)0x22, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var fingerprint = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var signPublicKey = Enumerable.Repeat((byte)0x33, SyncConstants.SyncDeltaEd25519PublicKeyBytes).ToArray();
        var agreementPublicKey = Enumerable.Repeat((byte)0x44, SyncConstants.SyncDeltaX25519PublicKeyBytes).ToArray();
        var deviceId = Guid.NewGuid();
        var originInstanceId = Guid.NewGuid();
        const DeviceType deviceType = DeviceType.WindowsPc;
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentResponseAuthenticatedBytes(
            123456789,
            nonce,
            "ABCDEFG2",
            deviceId,
            originInstanceId,
            deviceType,
            fingerprint,
            signPublicKey,
            agreementPublicKey,
            IPAddress.Parse("192.168.1.50"));
        var mac = LocalDiscoveryAuthenticator.ComputeMac(secret, authenticatedBytes);
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, SyncConstants.LocalDiscoveryMacBytes);

        MSTestAssert.IsTrue(LocalDiscoveryPacketCodec.TryDecodeEnrollmentResponse(payload, out var decoded));
        MSTestAssert.AreEqual(deviceId, decoded.DeviceId);
        MSTestAssert.AreEqual(originInstanceId, decoded.OriginInstanceId);
        MSTestAssert.AreEqual(deviceType, decoded.DeviceType);
        MSTestAssert.AreEqual("ABCDEFG2", decoded.SessionId);
        CollectionAssert.AreEqual(fingerprint, decoded.TlsFingerprint);
        CollectionAssert.AreEqual(signPublicKey, decoded.SignPublicKey);
        CollectionAssert.AreEqual(agreementPublicKey, decoded.AgreementPublicKey);
        MSTestAssert.AreEqual(IPAddress.Parse("192.168.1.50"), decoded.ResponderAddress);
        MSTestAssert.IsTrue(LocalDiscoveryAuthenticator.VerifyMac(secret, decoded.AuthenticatedBytes, decoded.Mac));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void PacketCodec_ArbitraryHostileDatagrams_DoNotThrow()
    {
        var random = new Random(123456);

        for (var i = 0; i < 1000; i++)
        {
            var payload = new byte[random.Next(0, SyncConstants.LocalDiscoveryMaxPacketBytes + 1)];
            random.NextBytes(payload);

            _ = LocalDiscoveryPacketCodec.TryReadMessageType(payload, out _);
            _ = LocalDiscoveryPacketCodec.TryDecodeSyncQuery(payload, out _);
            _ = LocalDiscoveryPacketCodec.TryDecodeSyncResponse(payload, out _);
            _ = LocalDiscoveryPacketCodec.TryDecodeEnrollmentQuery(payload, out _);
            _ = LocalDiscoveryPacketCodec.TryDecodeEnrollmentResponse(payload, out _);
        }
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void RecentNonceCache_RetainsNonceForEntireFreshnessAcceptanceWindow()
    {
        var cache = new RecentLocalDiscoveryNonceCache();
        var nonce = Enumerable.Repeat((byte)0x7A, SyncConstants.LocalDiscoveryNonceBytes).ToArray();
        var now = DateTimeOffset.UtcNow;

        MSTestAssert.IsTrue(cache.TryAdd("test", nonce, now));
        MSTestAssert.IsFalse(cache.TryAdd("test", nonce, now.AddSeconds(SyncConstants.LocalDiscoveryMaxClockSkewSeconds + 1)));
        MSTestAssert.IsTrue(cache.TryAdd("test", nonce, now.AddSeconds(SyncConstants.LocalDiscoveryReplayRetentionSeconds + 1)));
    }


    [TestMethod]
    [TestCategory("Backend")]
    [TestCategory("Unit")]
    public void PacketCodec_RejectsUnknownVersionAndOversizedPayload()
    {
        var nonce = new byte[SyncConstants.LocalDiscoveryNonceBytes];
        var authenticatedBytes = LocalDiscoveryPacketCodec.BuildEnrollmentQueryAuthenticatedBytes(123456789, nonce, "ABCDEFG2");
        var mac = new byte[SyncConstants.LocalDiscoveryMacBytes];
        var payload = LocalDiscoveryPacketCodec.AppendAuthenticator(authenticatedBytes, mac, SyncConstants.LocalDiscoveryMacBytes);

        var wrongVersion = payload.ToArray();
        wrongVersion[4] = 99;
        MSTestAssert.IsFalse(LocalDiscoveryPacketCodec.TryReadMessageType(wrongVersion, out _));

        var oversized = new byte[SyncConstants.LocalDiscoveryMaxPacketBytes + 1];
        MSTestAssert.IsFalse(LocalDiscoveryPacketCodec.TryReadMessageType(oversized, out _));
    }
}
