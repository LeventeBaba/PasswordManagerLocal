namespace PasswordManagerLocal.Windows.Frontend.AgentConnection;

public sealed class WindowsAgentControlWriteException : Exception
{
    public WindowsAgentControlWriteException(
        WindowsAgentControlWriteTransmissionState transmissionState,
        Exception innerException)
        : base("The Windows agent control write failed before an authoritative response was received.", innerException)
    {
        if (!Enum.IsDefined(transmissionState))
            throw new ArgumentOutOfRangeException(nameof(transmissionState));

        TransmissionState = transmissionState;
    }

    public WindowsAgentControlWriteTransmissionState TransmissionState { get; }
}
