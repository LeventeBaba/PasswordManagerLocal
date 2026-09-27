namespace PasswordManagerLocal.Windows.Ipc.Client;

public sealed class IpcRequestTransmissionException : Exception
{
    public IpcRequestTransmissionException(
        long correlationId,
        IpcRequestTransmissionState transmissionState,
        Exception innerException)
        : base("The IPC request failed before a conclusive response was received.", innerException)
    {
        if (correlationId < 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));
        if (!Enum.IsDefined(transmissionState))
            throw new ArgumentOutOfRangeException(nameof(transmissionState));

        CorrelationId = correlationId;
        TransmissionState = transmissionState;
    }

    public long CorrelationId { get; }
    public IpcRequestTransmissionState TransmissionState { get; }
}
