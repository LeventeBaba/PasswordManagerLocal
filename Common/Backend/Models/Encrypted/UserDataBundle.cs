namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class UserDataBundle : IDisposable
{
    private bool _disposed;

    public UserData UserData { get; set; } = new();
    public GeneralUserData GeneralUserData { get; set; } = new();
    public UserPasswordsData UserPasswordsData { get; set; } = new();
    public UserDevicesData UserDevicesData { get; set; } = new();

    public void Dispose()
    {
        if (_disposed)
            return;

        UserData.Dispose();
        GeneralUserData.Dispose();
        UserPasswordsData.Dispose();
        UserDevicesData.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
