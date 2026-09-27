namespace PasswordManagerLocal.Common.Backend.Hosting;

public interface IBackgroundSyncSettingsFileSystem
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void CreateDirectory(string path);
    void WriteAllText(string path, string contents);
    void ReplaceFile(string sourcePath, string destinationPath);
    void DeleteFile(string path);
}
