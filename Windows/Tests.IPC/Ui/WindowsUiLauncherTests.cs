using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Agent.Ui;
using PasswordManagerLocal.Windows.Ipc.Coordination;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;
using System.Diagnostics;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsUiLauncherTests
{
    [TestMethod]
    public async Task MissingExecutableReturnsSafeFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var launcher = new WindowsUiLauncher(directory, AgentLocalizationTestFactory.CreateEnglish(), new DelegateProcessLauncher(_ => true));

            var result = await launcher.LaunchAsync();

            Assert.AreEqual(UiLaunchResultKind.ExecutableNotFound, result.Kind);
            Assert.IsFalse(result.SafeMessage.Contains(directory, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LaunchUsesExecutablePathDirectlyWithoutCommandShell()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Password Manager {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, WindowsExecutableNames.UiExecutableFileName);
        File.WriteAllBytes(executable, Array.Empty<byte>());
        ProcessStartInfo? captured = null;
        var processLauncher = new DelegateProcessLauncher(startInfo =>
        {
            captured = startInfo;
            return true;
        });
        try
        {
            var launcher = new WindowsUiLauncher(directory, AgentLocalizationTestFactory.CreateEnglish(), processLauncher);

            var result = await launcher.LaunchAsync();

            Assert.AreEqual(UiLaunchResultKind.LaunchRequested, result.Kind);
            Assert.IsNotNull(captured);
            Assert.AreEqual(executable, captured.FileName);
            Assert.AreEqual(directory, captured.WorkingDirectory);
            Assert.IsTrue(captured.UseShellExecute);
            Assert.IsFalse(captured.FileName.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(captured.FileName.Contains("powershell", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [TestMethod]
    public async Task ProcessStartFailureReturnsSafeFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, WindowsExecutableNames.UiExecutableFileName),
            Array.Empty<byte>());
        try
        {
            var launcher = new WindowsUiLauncher(
                directory,
                AgentLocalizationTestFactory.CreateEnglish(),
                new DelegateProcessLauncher(_ => throw new InvalidOperationException("sensitive")));

            var result = await launcher.LaunchAsync();

            Assert.AreEqual(UiLaunchResultKind.LaunchFailed, result.Kind);
            Assert.IsFalse(result.SafeMessage.Contains("sensitive", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
