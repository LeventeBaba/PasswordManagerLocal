namespace PasswordManagerLocal.Common.Backend.Models.Encrypted;

public sealed class UserDataBundle : IDisposable
{
    private bool _disposed;

    // Process-local provenance; never serialized into vault data or sent to peers.
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? CanonicalGeneration { get; set; }


    public UserData UserData { get; set; } = new();
    public GeneralUserData GeneralUserData { get; set; } = new();
    public UserPasswordsData UserPasswordsData { get; set; } = new();
    public UserDevicesData UserDevicesData { get; set; } = new();

    public void Dispose()
    {
        if (_disposed)
            return;

        if (CanonicalGeneration is not null)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(CanonicalGeneration);
        CanonicalGeneration = null;
        UserData.Dispose();
        GeneralUserData.Dispose();
        UserPasswordsData.Dispose();
        UserDevicesData.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
