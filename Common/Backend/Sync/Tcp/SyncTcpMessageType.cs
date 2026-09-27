namespace PasswordManagerLocal.Common.Backend.Sync.Tcp;

public enum SyncTcpMessageType : byte
{
    HelloRequest = 1,
    HelloReply = 2,
    PushDeltaStart = 10,
    DeltaChunk = 11,
    PushDeltaEnd = 12,
    Ack = 13,
    GetDeviceEnrollmentInfoRequest = 20,
    GetDeviceEnrollmentInfoReply = 21,
    CompleteDeviceEnrollmentStart = 30,
    CompleteDeviceEnrollmentChunk = 31,
    CompleteDeviceEnrollmentEnd = 32,
    CompleteDeviceEnrollmentReply = 33,
    UserSnapshotInventoryRequest = 40,
    UserSnapshotInventoryReply = 41,
    UserSnapshotRequestBatch = 42,
    UserSnapshotRelayStart = 43,
    UserSnapshotRelayEnd = 44,
    UserControlOperationInventoryRequest = 45,
    UserControlOperationInventoryReply = 46,
    UserControlOperationRequestBatch = 47,
    UserControlOperationRelayStart = 48,
    UserControlOperationRelayEnd = 49,
    Error = 100
}
