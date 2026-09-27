using PasswordManagerLocal.Common.Contracts.Preferences;
using PasswordManagerLocal.Windows.Agent.Localization;

namespace PasswordManagerLocal.Windows.Tests.IPC.Infrastructure;

internal static class AgentLocalizationTestFactory
{
    public static AgentLocalizer CreateEnglish() =>
        AgentLocalizer.CreateAsync(AppLanguage.English).GetAwaiter().GetResult();
}
