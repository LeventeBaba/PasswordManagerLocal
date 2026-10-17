using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase17FrontendStatusPollingArchitectureTests
{
    [TestMethod]
    public void AuthenticationAndEnrollmentStatusHaveNoRecurringFrontendLoops()
    {
        var root = GetRepositoryRoot();
        var mainViewModel = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "ViewModels", "MainViewModel.cs"));
        var loginViewModel = File.ReadAllText(Path.Combine(
            root, "Common", "Frontend", "ViewModels", "Auth", "LoginViewModel.cs"));

        Assert.IsFalse(mainViewModel.Contains("DispatcherTimer", StringComparison.Ordinal));
        Assert.IsFalse(mainViewModel.Contains("EnsureSessionMonitor", StringComparison.Ordinal));
        Assert.IsFalse(mainViewModel.Contains("CheckAllSessionsAsync", StringComparison.Ordinal));
        Assert.IsFalse(loginViewModel.Contains("StartStatusPolling", StringComparison.Ordinal));
        Assert.IsFalse(loginViewModel.Contains("_deviceTransferPolling", StringComparison.Ordinal));
        Assert.IsFalse(loginViewModel.Contains("Task.Delay(TimeSpan.FromSeconds(2)", StringComparison.Ordinal));
        StringAssert.Contains(mainViewModel, "IFrontendDeadlineScheduler");
        StringAssert.Contains(loginViewModel, "IFrontendDeadlineScheduler");
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var startPaths = new[]
        {
            Path.GetDirectoryName(sourcePath),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var startPath in startPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            for (var directory = new DirectoryInfo(startPath!); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PasswordManagerLocal.sln")) &&
                    File.Exists(Path.Combine(directory.FullName, "Common", "Tests", "PasswordManagerLocal.Common.Tests.csproj")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from source path '{sourcePath}'.");
    }
}
