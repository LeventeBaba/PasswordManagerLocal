using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Preferences;
using PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

namespace PasswordManagerLocal.Windows.Tests.IPC.Preferences;

[TestClass]
public sealed class WindowsAgentApplicationPreferencesReaderTests
{
    [TestMethod]
    [DataRow(AppLanguage.English, AppThemeId.Light)]
    [DataRow(AppLanguage.English, AppThemeId.Dark)]
    [DataRow(AppLanguage.Hungarian, AppThemeId.Light)]
    [DataRow(AppLanguage.Hungarian, AppThemeId.Dark)]
    public async Task ReadLanguageReturnsStoredLanguageIndependentlyOfTheme(AppLanguage language, AppThemeId theme)
    {
        var store = new MutableApplicationPreferencesStore(language, theme);
        var reader = new WindowsAgentApplicationPreferencesReader(store);

        var actual = await reader.ReadLanguageAsync();

        Assert.AreEqual(language, actual);
        Assert.AreEqual(1, store.ReadCount);
        Assert.AreEqual(0, store.WriteCount);
    }
}
