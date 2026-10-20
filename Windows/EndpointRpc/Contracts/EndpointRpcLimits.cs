namespace PasswordManagerLocal.Windows.EndpointRpc.Contracts;

public static class EndpointRpcLimits
{
    public const int MaximumRequestPayloadSize = 512 * 1024;
    public const int MaximumResponsePayloadSize = 1536 * 1024;
    public const int MaximumSmallRequestPayloadSize = 64 * 1024;
    public const int MaximumBulkRequestPayloadSize = MaximumRequestPayloadSize;
    public const int MaximumSmallResponsePayloadSize = 128 * 1024;
    public const int MaximumCollectionResponsePayloadSize = 768 * 1024;
    public const int MaximumCollectionItems = 1000;
    public const int MaximumSensitiveBinaryFieldSize = 300;
    public const int MaximumSafeErrorMessageLength = 512;
    public const int MaximumErrorPayloadSize = 4 * 1024;
    public const int MaximumLargeResultChunkBytes = 512 * 1024;
    public const int MaximumLargeResultChunkResponsePayloadSize = 768 * 1024;
    public const int MaximumLargeResultTotalBytes = 64 * 1024 * 1024;
    public const int MaximumLargeResultChunkCount = 128;
    public const int MaximumConcurrentLargeTransfers = 2;
    public const int MaximumLargeTransfersPerConnection = 1;
    public const int LargeResultTransferLifetimeSeconds = 120;
    public const int LargeResultReleaseTimeoutSeconds = 5;
    public const int MaximumEnrollmentCodeLength = 4096;
    public const int MaximumFingerprintLength = 256;
    public const int MaximumTimeZoneIdLength = 256;
}
