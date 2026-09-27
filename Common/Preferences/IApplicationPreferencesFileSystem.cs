namespace PasswordManagerLocal.Common.Preferences;

public interface IApplicationPreferencesFileSystem
{
    bool FileExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    void CreateDirectory(string path);

    Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken);

    void ReplaceFile(string sourcePath, string destinationPath);

    void DeleteFile(string path);

    Task<IAsyncDisposable> AcquireExclusiveLockAsync(
        string lockPath,
        CancellationToken cancellationToken);
}
