using System.Text;

namespace PasswordManagerLocal.Common.Backend.Hosting;

public sealed class PhysicalBackgroundSyncSettingsFileSystem : IBackgroundSyncSettingsFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path, Encoding.UTF8);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllText(string path, string contents) =>
        File.WriteAllText(path, contents, new UTF8Encoding(false));

    public void ReplaceFile(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: true);

    public void DeleteFile(string path) => File.Delete(path);
}
