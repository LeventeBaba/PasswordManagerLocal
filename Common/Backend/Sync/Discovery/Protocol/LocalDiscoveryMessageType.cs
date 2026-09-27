namespace PasswordManagerLocal.Common.Backend.Sync.Discovery.Protocol;

internal enum LocalDiscoveryMessageType : byte
{
    SyncQuery = 1,
    SyncResponse = 2,
    EnrollmentQuery = 3,
    EnrollmentResponse = 4
}
