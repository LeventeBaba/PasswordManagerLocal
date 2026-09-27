namespace PasswordManagerLocal.Windows.Agent.Hosting;

internal sealed record TrayExitRegistrationPreflight(bool CanProceed, string? SafeMessage)
{
    public static TrayExitRegistrationPreflight Proceed { get; } = new(true, null);

    public static TrayExitRegistrationPreflight Reject(string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return new TrayExitRegistrationPreflight(false, safeMessage);
    }
}