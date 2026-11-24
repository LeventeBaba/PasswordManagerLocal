using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Backend.Persistence;
using PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static PasswordManagerLocal.Common.Tests.TestInfrastructure.Storage.SqliteStorageTestCommands;

namespace PasswordManagerLocal.Common.Tests.Backend.Persistence;

[TestClass]
[TestCategory("Backend")]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SqliteProviderInitializerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateBundleInitializationAndCollection_PreserveLiveCallbacksAcrossReopen(bool pooling)
    {
        await using var fixture = await SqliteStorageFileFixture.CreateAsync();
        await InsertRowAsync(fixture.Anchor, "Devices", Guid.NewGuid());
        var options = new SqliteConnectionStringBuilder(fixture.Anchor.ConnectionString) { Pooling = pooling };
        var factory = new SqliteConnectionFactory(options.ToString());
        await using var connection = factory.CreateConnection();
        try
        {
            await connection.OpenAsync();
            for (var round = 0; round < 4; round++)
            {
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                    Task.Run(SqliteProviderInitializer.EnsureInitialized)));
                // A second fixture represents another host initializing while the
                // original native handle still owns the admission callback.
                await using var other = await SqliteStorageFileFixture.CreateAsync();
                await InsertRowAsync(other.Anchor, "Devices", Guid.NewGuid());
                // Deliberately exercise the package entry point here: late bundle
                // initialization must not replace the frozen process provider.
                SQLitePCL.Batteries_V2.Init();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                Assert.AreEqual(1L, await ScalarAsync(fixture.Anchor, "SELECT pml_wal_admit();"));
                Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT pml_wal_admit();"));
                await ExecuteAsync(connection, "UPDATE Devices SET InvalidSyncAttemptCount = InvalidSyncAttemptCount + 1;");
                Assert.AreEqual(round + 2L, await ScalarAsync(fixture.Anchor, "SELECT InvalidSyncAttemptCount FROM Devices;"));
                Assert.AreEqual(1L, await ScalarAsync(other.Anchor, "SELECT pml_wal_admit();"));
                await connection.CloseAsync();
                await connection.OpenAsync();
            }
        }
        finally
        {
            // Drain only this fixture's pool before its temporary directory is removed.
            SqliteConnection.ClearPool(connection);
        }
    }

    [TestMethod]
    public void ApplicationAndFixturesHaveOneProviderRegistrationOwner()
    {
        var root = GetRepositoryRoot();
        var owner = Path.Combine(root, "Common", "Backend", "Persistence", "SqliteProviderInitializer.cs");
        var regression = Path.Combine(root, "Common", "Tests", "Backend", "Persistence", "SqliteProviderInitializerTests.cs");
        var directRegistration = new Regex(
            @"\b(?:Batteries(?:_V2)?\s*\.\s*Init|raw\s*\.\s*(?:SetProvider|FreezeProvider))\s*\(");
        var violations = new[] { "Common", "Windows", "Android" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => path != owner && path != regression &&
                !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => directRegistration.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();
        Assert.IsEmpty(violations, "Provider replacement can invalidate live native callbacks: " +
            string.Join(Environment.NewLine, violations));
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "..", ".."));
}
