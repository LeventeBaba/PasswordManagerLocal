using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Windows.Frontend.Settings;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class WindowsAgentApplicationPreferencesNotifierTests
{
    [TestMethod]
    public async Task LanguageNotificationSendsPayloadFreeReloadThroughControlConnection()
    {
        var connection = new FakeWindowsAgentControlConnection();
        var notifier = new WindowsAgentApplicationPreferencesNotifier(connection);

        await notifier.NotifyLanguagePersistedAsync();

        Assert.AreEqual(1, connection.ReloadApplicationPreferencesCount);
    }
    [TestMethod]
    public async Task RejectedReloadIsReportedAsNotificationFailure()
    {
        var connection = new FakeWindowsAgentControlConnection
        {
            ReloadApplicationPreferencesResult = false
        };
        var notifier = new WindowsAgentApplicationPreferencesNotifier(connection);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => notifier.NotifyLanguagePersistedAsync());

        Assert.AreEqual(1, connection.ReloadApplicationPreferencesCount);
    }

}
