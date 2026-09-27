using PasswordManagerLocal.Windows.Ipc.Contracts;

namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed record IpcTransmissionAwareResponse(
    long CorrelationId,
    IpcRequestTransmissionState TransmissionState,
    IpcResponseEnvelope Response);
