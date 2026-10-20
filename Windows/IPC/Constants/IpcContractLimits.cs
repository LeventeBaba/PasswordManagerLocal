namespace PasswordManagerLocal.Windows.Ipc.Constants;

public static class IpcContractLimits
{
    public const int MaximumSafeMessageLength = 1024;
    public const int MaximumErrorPayloadSize = 8 * 1024;
    public const int MaximumInnerPayloadSize = 1600 * 1024;
}
