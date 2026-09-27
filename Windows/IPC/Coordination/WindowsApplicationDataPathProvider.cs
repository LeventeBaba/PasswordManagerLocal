namespace PasswordManagerLocal.Windows.Ipc.Coordination;

public sealed class WindowsApplicationDataPathProvider
{
    private readonly string _applicationFolderName;

    public WindowsApplicationDataPathProvider(string applicationFolderName = "PasswordManagerLocal")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationFolderName);
        _applicationFolderName = applicationFolderName;
    }

    public string GetApplicationDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new InvalidOperationException("The Windows local application-data directory is unavailable.");

        return Directory.CreateDirectory(
            Path.Combine(localApplicationData, _applicationFolderName)).FullName;
    }
}
