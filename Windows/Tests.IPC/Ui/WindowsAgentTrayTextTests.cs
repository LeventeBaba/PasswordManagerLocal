using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Tray;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsAgentTrayTextTests
{
    [TestMethod]
    [DataRow(AppLanguage.English, "Open Password Manager Local", "Exit", "The Password Manager Local agent could not start.")]
    [DataRow(AppLanguage.Hungarian, "Password Manager Local megnyitása", "Kilépés", "A Password Manager Local háttérügynöke nem tudott elindulni.")]
    public async Task EmbeddedLanguageMapsToExpectedTrayLabels(
        AppLanguage language, string openLabel, string exitLabel, string startupFailure)
    {
        var localizer = await AgentLocalizer.CreateAsync(language);

        var text = WindowsAgentTrayText.Create(localizer);

        Assert.AreEqual(openLabel, text.OpenLabel);
        Assert.AreEqual(exitLabel, text.ExitLabel);
        Assert.AreEqual(startupFailure, text.StartupFailureMessage);
    }

}
