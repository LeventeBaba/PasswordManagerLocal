using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Configuration;
using PasswordManagerLocal.Common.Backend.Constants;

namespace PasswordManagerLocal.Common.Tests.Backend.Configuration;

[TestClass]
public sealed class BackendStoragePathsTests
{
    [TestMethod]
    public void Constructor_UsesLogsSubdirectoryWithoutCreatingIt()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"PasswordManagerLocal.StoragePaths.Tests.{Guid.NewGuid():N}");

        try
        {
            var paths = new BackendStoragePaths(rootDirectory);

            Assert.AreEqual(
                Path.Combine(paths.RootDirectory, ApplicationFileNames.LogsFolderName),
                paths.LogsDirectory);
            Assert.IsFalse(
                Directory.Exists(paths.LogsDirectory),
                "The Logs directory must be created only by Debug logging initialization.");
        }
        finally
        {
            try
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
