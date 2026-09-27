using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Exceptions;
using PasswordManagerLocal.Common.Backend.Security;
using PasswordManagerLocal.Common.Tests.Fakes;

namespace PasswordManagerLocal.Common.Tests.Backend.Security;

[TestClass]
public sealed class DbConfigManagerTests
{
    [TestMethod]
    public void TemporaryKeyUnavailability_IsNotTranslatedToDatabaseCompatibilityFailure()
    {
        using var directory = new TemporaryDirectory();
        var paths = new BackendStoragePaths(directory.Path);
        var protector = new SwitchableKeyProtector();

        _ = DbConfigManager.GetOrCreateSqlCipherPassword(paths, protector);
        protector.IsTemporarilyUnavailable = true;

        Assert.Throws<KeyProtectorUnavailableException>(
            () => DbConfigManager.GetOrCreateSqlCipherPassword(paths, protector));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"PasswordManagerLocal.DbConfig.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
