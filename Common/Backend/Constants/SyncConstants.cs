using PasswordManagerLocal.Common.Contracts.Sync;

namespace PasswordManagerLocal.Common.Backend.Constants;

public static class SyncConstants
{
    public const string PFXPassword = "";

    public const int SyncPort = SyncProtocolDefaults.TcpPort;
    public const int SyncProtocolVersion = 12;
    public const string LocalDiscoveryMulticastAddress = "239.255.67.67";
    public const int LocalDiscoveryPort = 26689;
    public const int LocalDiscoveryProtocolVersion = 2;
    public const int LocalDiscoveryPeerThrottleSeconds = 20;
    public const int LocalDiscoveryEnrollmentQueryIntervalSeconds = 2;
    public const int LocalDiscoveryMaxClockSkewSeconds = 60;
    public const int LocalDiscoveryRequestThrottleMilliseconds = 250;
    public const int LocalDiscoveryMaxPacketBytes = 2048;
    public const int LocalDiscoveryReplayRetentionSeconds = LocalDiscoveryMaxClockSkewSeconds * 2 + 5;
    public const int LocalDiscoveryNonceBytes = 16;
    public const int LocalDiscoverySignatureBytes = 64;
    public const int LocalDiscoveryMacBytes = 32;
    public const int LocalDiscoveryRecentNonceCapacity = 4096;
    public const int NetworkRefreshDebounceSeconds = 3;
    public const int DeviceEnrollmentDiscoveryTimeoutSeconds = 30;
    public const int DeviceEnrollmentConnectTimeoutSeconds = 8;
    public const int DeviceEnrollmentTransferTimeoutSeconds = 120;

    public const int MaxIncomingDeltaPayloadBytes = 4 * 1024 * 1024;
    public const int MaxUserSnapshotEnvelopeBytes = MaxIncomingDeltaPayloadBytes;
    public const int MaxUserControlOperationPayloadBytes = MaxIncomingDeltaPayloadBytes;
    public const int MaxUserControlOperationEnvelopeBytes = MaxIncomingDeltaPayloadBytes;
    public const int MaxIncomingDeltaTotalBytesPerCall = 32 * 1024 * 1024;
    public const int MaxDeviceEnrollmentSnapshotBytes = 64 * 1024 * 1024;
    public const int DeviceEnrollmentSnapshotChunkBytes = 64 * 1024;
    public const int PendingDeviceDeltaBatchSize = 32;
    // Limits the normal peak to six child-blob verification pipelines.
    public const int MaxConcurrentSnapshotVerifications = 2;

    public const int EncryptedUserDataFormatVersion = 2;
    public const int DeviceEnrollmentPayloadVersion = 5;

    public const int SyncDeltaEncryptionVersion = 1;
    public const int SyncDeltaNonceBytes = 12;
    public const int SyncDeltaTagBytes = 16;
    public const int SyncDeltaPayloadHashBytes = 32;
    public const int SyncDeltaX25519PublicKeyBytes = 32;
    public const int SyncDeltaEd25519PublicKeyBytes = 32;
    public const int SyncDeltaEd25519SignatureBytes = 64;
    public const int MaxIncomingDeltaCountPerCall = 256;
    public const int MaxUserSnapshotInventoryUsers = 256;
    public const int MaxUserSnapshotInventoryEntries = 4096;
    public const int MaxUserSnapshotRequestsPerCall = 256;
    public const int MaxUserControlInventoryUsers = 256;
    public const int MaxUserControlInventoryEntries = 4096;
    public const int MaxUserControlRequestsPerCall = 256;
    public const int MaxSyncTcpFrameBytes = MaxIncomingDeltaPayloadBytes + 4096;
    public const int MaxIncomingDeltaFutureSeconds = 300;
    public const int MaxInvalidIncomingSyncAttempts = 5;
    public const int MaxRecentIncomingDeltaReplayIds = 4096;
    public const int RecentIncomingDeltaReplayWindowMinutes = 30;

    public const int MaxConcurrentSyncConnections = 8;
    public const int MaxSyncConnectionsPerRemoteIp = 2;
    public const int SyncTcpHandshakeTimeoutSeconds = 10;
    public const int SyncTcpIdleTimeoutSeconds = 30;
    public const int SyncTcpWriteTimeoutSeconds = 30;
    public const int DevicePresenceProbeTimeoutSeconds = 4;

    public const int EnrollmentSnapshotEncryptionVersion = 4;
    public const int EnrollmentSnapshotEncryptionNonceBytes = 12;
    public const int EnrollmentSnapshotEncryptionTagBytes = 16;
    public const int EnrollmentCodeNoiseBytes = 16;
    public const int MaxEnrollmentValidationAttempts = 3;
}
