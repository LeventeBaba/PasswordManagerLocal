namespace PasswordManagerLocal.Windows.EndpointRpc.Client;

public sealed class EndpointRpcTransportException : Exception
{
    public EndpointRpcTransportException(
        long correlationId,
        EndpointRpcTransmissionState transmissionState,
        Exception innerException)
        : base("The endpoint RPC transport failed before a conclusive response was received.", innerException)
    {
        if (correlationId < 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));
        if (!Enum.IsDefined(transmissionState))
            throw new ArgumentOutOfRangeException(nameof(transmissionState));

        CorrelationId = correlationId;
        TransmissionState = transmissionState;
    }

    public long CorrelationId { get; }
    public EndpointRpcTransmissionState TransmissionState { get; }
}
