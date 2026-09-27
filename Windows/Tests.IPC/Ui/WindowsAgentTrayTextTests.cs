using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;
using PasswordManagerLocal.Windows.Agent.Tray;

namespace PasswordManagerLocal.Windows.Tests.IPC.Ui;

[TestClass]
public sealed class WindowsAgentTrayTextTests
{
    [TestMethod]
    public async Task EnglishTextComesFromAgentResource()
    {
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.English);
        var text = WindowsAgentTrayText.Create(localizer);

        Assert.AreEqual("Open PasswordManagerLocal", text.OpenLabel);
        Assert.AreEqual("Exit", text.ExitLabel);
        Assert.AreEqual("The PasswordManagerLocal agent could not start.", text.StartupFailureMessage);
    }

    [TestMethod]
    public async Task HungarianTextComesFromAgentResourceAndPreservesUnicode()
    {
        var localizer = await AgentLocalizer.CreateAsync(AppLanguage.Hungarian);
        var text = WindowsAgentTrayText.Create(localizer);

        Assert.AreEqual("PasswordManagerLocal megnyitása", text.OpenLabel);
        Assert.AreEqual("Kilépés", text.ExitLabel);
        Assert.AreEqual(
            "A PasswordManagerLocal háttérügynöke nem tudott elindulni.",
            text.StartupFailureMessage);
    }
}
