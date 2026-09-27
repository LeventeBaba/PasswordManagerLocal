using PasswordManagerLocal.Common.Contracts.Preferences;

namespace PasswordManagerLocal.Windows.Agent.Localization;

public interface IAgentLocalizer
{
    AppLanguage CurrentLanguage { get; }
    string GetString(string key);
}
