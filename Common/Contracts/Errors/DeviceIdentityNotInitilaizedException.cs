namespace PasswordManagerLocal.Common.Contracts.Errors;

public sealed class DeviceIdentityNotInitilaizedException : InvalidOperationException
{
    public DeviceIdentityNotInitilaizedException()
        : base("Device identity is not initialized.")
    {
    }
}
